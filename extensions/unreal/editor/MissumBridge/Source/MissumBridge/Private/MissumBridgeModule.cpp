#include "MissumBridgeCommands.h"

#include "Common/TcpListener.h"
#include "Containers/Queue.h"
#include "Containers/Ticker.h"
#include "Editor.h"
#include "Engine/World.h"
#include "FileHelpers.h"
#include "GameMapsSettings.h"
#include "HAL/PlatformProcess.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"
#include "Misc/FileHelper.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/SecureHash.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "SocketSubsystem.h"
#include "Sockets.h"

namespace
{
constexpr uint32 MaxRequestBytes = 1024 * 1024;

struct FPendingReply
{
    FEvent* Event = FPlatformProcess::GetSynchEventFromPool(true);
    TSharedPtr<FJsonObject> Value;
    ~FPendingReply() { FPlatformProcess::ReturnSynchEventToPool(Event); }
};

TSharedPtr<FJsonObject> BridgeError(const FString& Code, const FString& Message)
{
    TSharedPtr<FJsonObject> Detail = MakeShared<FJsonObject>();
    Detail->SetStringField(TEXT("code"), Code);
    Detail->SetStringField(TEXT("message"), Message);
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetBoolField(TEXT("ok"), false);
    Result->SetObjectField(TEXT("error"), Detail);
    return Result;
}

FString JsonText(const TSharedPtr<FJsonObject>& Value)
{
    FString Output;
    TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Output);
    FJsonSerializer::Serialize(Value.ToSharedRef(), Writer);
    return Output;
}
}

class FMissumBridgeModule final : public IModuleInterface
{
public:
    virtual void StartupModule() override
    {
        Token = FPlatformMisc::GetEnvironmentVariable(TEXT("MISSUM_UNREAL_TOKEN"));
        ReadyPath = FPlatformMisc::GetEnvironmentVariable(TEXT("MISSUM_UNREAL_READY_PATH"));
        if (Token.IsEmpty() || ReadyPath.IsEmpty()) return;
        bInitializeBlankProject = FPlatformMisc::GetEnvironmentVariable(TEXT("MISSUM_UNREAL_INITIALIZE_BLANK_PROJECT")) == TEXT("1");

        InstanceId = FGuid::NewGuid().ToString(EGuidFormats::Digits);
        ISocketSubsystem* Sockets = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
        ListenerSocket = Sockets ? Sockets->CreateSocket(NAME_Stream, TEXT("MissumBridge"), false) : nullptr;
        if (!ListenerSocket) return;
        const FIPv4Endpoint Endpoint(FIPv4Address::InternalLoopback, 0);
        if (!ListenerSocket->Bind(*Endpoint.ToInternetAddr()) || !ListenerSocket->Listen(8))
        {
            Sockets->DestroySocket(ListenerSocket);
            ListenerSocket = nullptr;
            return;
        }
        TSharedRef<FInternetAddr> Bound = Sockets->CreateInternetAddr();
        ListenerSocket->GetAddress(*Bound);
        Port = Bound->GetPort();
        Listener = MakeUnique<FTcpListener>(*ListenerSocket);
        Listener->OnConnectionAccepted().BindRaw(this, &FMissumBridgeModule::Accept);
        TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FMissumBridgeModule::Tick));

        if (!bInitializeBlankProject) PublishReady(true, FString());
    }

    void PublishReady(bool Initialized, const FString& InitializationError)
    {
        TSharedPtr<FJsonObject> Ready = MakeShared<FJsonObject>();
        Ready->SetNumberField(TEXT("port"), Port);
        Ready->SetNumberField(TEXT("processId"), FPlatformProcess::GetCurrentProcessId());
        Ready->SetStringField(TEXT("token"), Token);
        Ready->SetStringField(TEXT("instanceId"), InstanceId);
        Ready->SetStringField(TEXT("projectPath"), FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath()));
        Ready->SetBoolField(TEXT("initialized"), Initialized);
        if (!InitializationError.IsEmpty()) Ready->SetStringField(TEXT("initializationError"), InitializationError);
        const FString Temporary = ReadyPath + TEXT(".tmp");
        if (FFileHelper::SaveStringToFile(JsonText(Ready), *Temporary))
            IFileManager::Get().Move(*ReadyPath, *Temporary, true, true);
    }

    virtual void ShutdownModule() override
    {
        if (Listener) Listener->Stop();
        Listener.Reset();
        FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
        if (ListenerSocket)
        {
            ListenerSocket->Close();
            ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->DestroySocket(ListenerSocket);
            ListenerSocket = nullptr;
        }
    }

private:
    bool Tick(float)
    {
        if (bInitializeBlankProject && GEditor && GEditor->GetEditorWorldContext().World())
        {
            bInitializeBlankProject = false;
            const FString Error = InitializeBlankProject();
            PublishReady(Error.IsEmpty(), Error);
        }
        TFunction<void()> Request;
        while (Requests.Dequeue(Request)) Request();
        return true;
    }

    FString InitializeBlankProject()
    {
        const FString AssetPath = TEXT("/Game/Maps/Main");
        const FString Filename = FPackageName::LongPackageNameToFilename(AssetPath, FPackageName::GetMapPackageExtension());
        const FString ReceiptDirectory = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("Missum"));
        const FString IntentPath = ReceiptDirectory / TEXT("blank-main.intent");
        const FString ReceiptPath = ReceiptDirectory / TEXT("blank-main.md5");
        if (!IFileManager::Get().MakeDirectory(*ReceiptDirectory, true))
            return TEXT("Der Nachweisordner für die Startkarte konnte nicht erstellt werden.");
        const bool Exists = FPackageName::DoesPackageExist(AssetPath) || IFileManager::Get().FileExists(*Filename);
        if (Exists)
        {
            FString PreviousHash;
            const FMD5Hash Hash = FMD5Hash::HashFile(*Filename);
            if (!IFileManager::Get().FileExists(*IntentPath)
                || !FFileHelper::LoadFileToString(PreviousHash, *ReceiptPath)
                || !Hash.IsValid() || PreviousHash.TrimStartAndEnd() != BytesToHex(Hash.GetBytes(), Hash.GetSize()))
                return TEXT("Die Startkarte /Game/Maps/Main existiert bereits und gehört nicht nachweisbar zu dieser Einrichtung. Sie wurde nicht überschrieben.");
            if (!UEditorLoadingAndSavingUtils::LoadMap(Filename))
                return TEXT("Die bereits erstellte leere Startkarte konnte nicht erneut geöffnet werden.");
        }
        else
        {
            if (!IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true))
                return TEXT("Der Ordner für die leere Startkarte konnte nicht erstellt werden.");
            if (!FFileHelper::SaveStringToFile(AssetPath, *IntentPath))
                return TEXT("Der Einrichtungsnachweis für die leere Startkarte konnte nicht geschrieben werden.");
            UWorld* World = UEditorLoadingAndSavingUtils::NewBlankMap(false);
            if (!World || !UEditorLoadingAndSavingUtils::SaveMap(World, AssetPath))
                return TEXT("Die leere Startkarte konnte nicht erstellt oder gespeichert werden.");
            const FMD5Hash Hash = FMD5Hash::HashFile(*Filename);
            if (!Hash.IsValid() || !FFileHelper::SaveStringToFile(BytesToHex(Hash.GetBytes(), Hash.GetSize()), *ReceiptPath))
                return TEXT("Die Startkarte wurde gespeichert, ihr Einrichtungsnachweis konnte jedoch nicht geschrieben werden.");
        }

        const FString ConfigFile = FPaths::ConvertRelativePathToFull(FPaths::ProjectConfigDir() / TEXT("DefaultEngine.ini"));
        const TCHAR* Section = TEXT("/Script/EngineSettings.GameMapsSettings");
        const TCHAR* FullMapPath = TEXT("/Game/Maps/Main.Main");
        FConfigFile ProjectConfig;
        ProjectConfig.Read(ConfigFile);
        ProjectConfig.SetString(Section, TEXT("EditorStartupMap"), FullMapPath);
        ProjectConfig.SetString(Section, TEXT("GameDefaultMap"), FullMapPath);
        if (!ProjectConfig.Write(ConfigFile))
            return TEXT("Die leere Startkarte wurde gespeichert, die Projekteinstellungen konnten jedoch nicht geschrieben werden.");
        FConfigFile Verify;
        Verify.Read(ConfigFile);
        FString EditorMap;
        FString GameMap;
        if (!Verify.GetString(Section, TEXT("EditorStartupMap"), EditorMap) || EditorMap != FullMapPath
            || !Verify.GetString(Section, TEXT("GameDefaultMap"), GameMap) || GameMap != FullMapPath)
            return TEXT("Die Startkarteneinstellungen konnten nach dem Speichern nicht bestätigt werden.");
        GetMutableDefault<UGameMapsSettings>()->EditorStartupMap = FSoftObjectPath(FullMapPath);
        UGameMapsSettings::SetGameDefaultMap(FullMapPath);
        return FString();
    }

    bool Accept(FSocket* Socket, const FIPv4Endpoint&)
    {
        // Listener invokes this on its own thread. Unreal editor objects are
        // accessed only inside the dispatched game-thread closure below.
        Socket->SetNonBlocking(false);
        const double Deadline = FPlatformTime::Seconds() + 15.0;
        TArray<uint8> Data;
        bool Complete = false;
        while (FPlatformTime::Seconds() < Deadline && Data.Num() < MaxRequestBytes)
        {
            if (!Socket->Wait(ESocketWaitConditions::WaitForRead, FTimespan::FromMilliseconds(100))) continue;
            uint8 Buffer[4096];
            int32 Read = 0;
            if (!Socket->Recv(Buffer, sizeof(Buffer), Read) || Read <= 0) break;
            for (int32 Index = 0; Index < Read; ++Index)
            {
                if (Buffer[Index] == '\n') { Complete = true; break; }
                Data.Add(Buffer[Index]);
            }
            if (Complete) break;
        }
        TSharedPtr<FJsonObject> Reply;
        FString RequestId;
        if (Complete && Data.Num() < MaxRequestBytes)
        {
            Data.Add(0);
            const FString Text = UTF8_TO_TCHAR(reinterpret_cast<const char*>(Data.GetData()));
            TSharedPtr<FJsonObject> Request;
            TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
            FString SuppliedToken;
            FString Command;
            if (FJsonSerializer::Deserialize(Reader, Request) && Request.IsValid()
                && Request->TryGetStringField(TEXT("token"), SuppliedToken)
                && SuppliedToken == Token && Request->TryGetStringField(TEXT("command"), Command))
            {
                Request->TryGetStringField(TEXT("requestId"), RequestId);
                const TSharedPtr<FJsonObject>* Arguments = nullptr;
                Request->TryGetObjectField(TEXT("arguments"), Arguments);
                TSharedPtr<FJsonObject> Args = Arguments ? *Arguments : MakeShared<FJsonObject>();
                TSharedPtr<FPendingReply, ESPMode::ThreadSafe> Pending = MakeShared<FPendingReply, ESPMode::ThreadSafe>();
                const FString TargetInstance = InstanceId;
                Requests.Enqueue([Pending, Command, Args, TargetInstance]()
                {
                    Pending->Value = MissumExecuteCommand(Command, Args, TargetInstance);
                    Pending->Event->Trigger();
                });
                if (Pending->Event->Wait(30000)) Reply = Pending->Value;
                else Reply = BridgeError(TEXT("unreal.timeout"), TEXT("Editor antwortet nicht; Vorgangsstatus vor Wiederholung prüfen."));
            }
            else Reply = BridgeError(TEXT("unreal.invalid_request"), TEXT("Ungültige oder nicht autorisierte Anfrage."));
        }
        if (!Reply.IsValid()) Reply = BridgeError(TEXT("unreal.invalid_request"), TEXT("Anfrage unvollständig oder zu groß."));
        Reply->SetStringField(TEXT("requestId"), RequestId);
        FTCHARToUTF8 Encoded(*(JsonText(Reply) + TEXT("\n")));
        int32 Total = 0;
        while (Total < Encoded.Length())
        {
            int32 Sent = 0;
            if (!Socket->Send(reinterpret_cast<const uint8*>(Encoded.Get()) + Total, Encoded.Length() - Total, Sent) || Sent <= 0) break;
            Total += Sent;
        }
        Socket->Close();
        ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->DestroySocket(Socket);
        return true;
    }

    FString Token;
    FString InstanceId;
    FString ReadyPath;
    bool bInitializeBlankProject = false;
    FSocket* ListenerSocket = nullptr;
    int32 Port = 0;
    TUniquePtr<FTcpListener> Listener;
    TQueue<TFunction<void()>, EQueueMode::Mpsc> Requests;
    FTSTicker::FDelegateHandle TickHandle;
};

IMPLEMENT_MODULE(FMissumBridgeModule, MissumBridge)
