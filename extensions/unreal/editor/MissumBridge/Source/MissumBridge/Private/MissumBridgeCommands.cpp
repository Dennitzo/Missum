#include "MissumBridgeCommands.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Camera/CameraActor.h"
#include "Camera/CameraComponent.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/ActorComponent.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphNode.h"
#include "EdGraphSchema_K2.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/World.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/SimpleConstructionScript.h"
#include "Engine/SCS_Node.h"
#include "Engine/TextureRenderTarget2D.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "K2Node_CallFunction.h"
#include "K2Node_Event.h"
#include "K2Node_VariableGet.h"
#include "K2Node_VariableSet.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/CompilerResultsLog.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "ImageUtils.h"
#include "LevelEditorViewport.h"
#include "Misc/FileHelper.h"
#include "Misc/EngineVersion.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/SecureHash.h"
#include "Serialization/JsonSerializer.h"
#include "ScopedTransaction.h"
#include "RenderingThread.h"
#include "UObject/SavePackage.h"
#include "UnrealClient.h"

namespace
{
TMap<FString, FMD5Hash> PackageRevisions;
TMap<FString, TSharedPtr<FJsonObject>> Captures;
TMap<FString, TPair<FString, TSharedPtr<FJsonObject>>> CompletedOperations;

TSharedPtr<FJsonObject> Fail(const FString& Code, const FString& Message)
{
    TSharedPtr<FJsonObject> Detail = MakeShared<FJsonObject>();
    Detail->SetStringField(TEXT("code"), Code);
    Detail->SetStringField(TEXT("message"), Message);
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetBoolField(TEXT("ok"), false);
    Result->SetObjectField(TEXT("error"), Detail);
    return Result;
}

TSharedPtr<FJsonObject> Success(const TSharedPtr<FJsonObject>& Value)
{
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetBoolField(TEXT("ok"), true);
    Result->SetObjectField(TEXT("result"), Value);
    return Result;
}

FString String(const TSharedPtr<FJsonObject>& Args, const TCHAR* Name)
{
    FString Value;
    Args->TryGetStringField(Name, Value);
    return Value;
}

TSharedPtr<FJsonObject> VectorValue(const FVector& Value)
{
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetNumberField(TEXT("x"), Value.X);
    Result->SetNumberField(TEXT("y"), Value.Y);
    Result->SetNumberField(TEXT("z"), Value.Z);
    return Result;
}

TSharedPtr<FJsonObject> RotationValue(const FRotator& Value)
{
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetNumberField(TEXT("pitch"), Value.Pitch);
    Result->SetNumberField(TEXT("yaw"), Value.Yaw);
    Result->SetNumberField(TEXT("roll"), Value.Roll);
    return Result;
}

TSharedPtr<FJsonObject> CaptureView(const TSharedPtr<FJsonObject>& Args, const FString& InstanceId)
{
    const FString OperationId = String(Args, TEXT("operationId"));
    const FString Source = String(Args, TEXT("source"));
    const FString CameraName = String(Args, TEXT("cameraName"));
    FString Root = FPlatformMisc::GetEnvironmentVariable(TEXT("MISSUM_UNREAL_CAPTURE_ROOT"));
    FString Path = String(Args, TEXT("outputPath"));
    if (OperationId.IsEmpty() || Root.IsEmpty() || Path.IsEmpty() || FPaths::IsRelative(Path))
        return Fail(TEXT("unreal.capture_arguments"), TEXT("Aufnahme-ID und absoluter Ausgabepfad fehlen."));
    Root = FPaths::ConvertRelativePathToFull(Root);
    Path = FPaths::ConvertRelativePathToFull(Path);
    FPaths::NormalizeDirectoryName(Root);
    FPaths::NormalizeFilename(Path);
    if (!FPaths::CollapseRelativeDirectories(Root) || !FPaths::CollapseRelativeDirectories(Path)
        || !FPaths::IsUnderDirectory(Path, Root) || FPaths::GetExtension(Path).ToLower() != TEXT("png")
        || IFileManager::Get().FileExists(*Path))
        return Fail(TEXT("unreal.capture_path"), TEXT("Das Bildziel liegt nicht im Aufnahmeordner oder existiert bereits."));
    UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
    if (!World) return Fail(TEXT("unreal.world_missing"), TEXT("Es ist keine Editor-Welt geöffnet."));
    FVector Position;
    FRotator Rotation;
    float FieldOfView = 90.0f;
    FString CameraIdentity;
    UCameraComponent* SelectedCamera = nullptr;
    if (Source == TEXT("camera"))
    {
        ACameraActor* Found = nullptr;
        for (TActorIterator<ACameraActor> It(World); It; ++It)
        {
            if (It->GetName() != CameraName && It->GetPathName() != CameraName && It->GetActorLabel() != CameraName) continue;
            if (Found) return Fail(TEXT("unreal.camera_ambiguous"), TEXT("Mehrere Kameras entsprechen dem Namen; vollständigen Pfad verwenden."));
            Found = *It;
        }
        if (!Found || !Found->GetCameraComponent())
            return Fail(TEXT("unreal.camera_missing"), TEXT("Die ausgewählte Unreal-Kamera wurde nicht gefunden."));
        UCameraComponent* Camera = Found->GetCameraComponent();
        SelectedCamera = Camera;
        Position = Camera->GetComponentLocation();
        Rotation = Camera->GetComponentRotation();
        FieldOfView = Camera->FieldOfView;
        CameraIdentity = Found->GetPathName();
    }
    else if (Source == TEXT("viewport"))
    {
        if (!GCurrentLevelEditingViewportClient || !GCurrentLevelEditingViewportClient->Viewport)
            return Fail(TEXT("unreal.viewport_missing"), TEXT("Es ist kein Editor-Viewport verfügbar."));
        Position = GCurrentLevelEditingViewportClient->GetViewLocation();
        Rotation = GCurrentLevelEditingViewportClient->GetViewRotation();
        FieldOfView = GCurrentLevelEditingViewportClient->ViewFOV;
        CameraIdentity = TEXT("editor-viewport");
    }
    else return Fail(TEXT("unreal.capture_source"), TEXT("Quelle muss viewport oder camera sein."));

    TArray<FColor> Pixels;
    FIntPoint Size(1280, 720);
    bool Read = false;
    if (Source == TEXT("viewport"))
    {
        FViewport* Viewport = GCurrentLevelEditingViewportClient->Viewport;
        Size = Viewport->GetSizeXY();
        if (Size.X <= 0 || Size.Y <= 0 || static_cast<int64>(Size.X) * Size.Y > 16000000)
            return Fail(TEXT("unreal.viewport_unavailable"), TEXT("Viewport ist leer oder zu groß für eine Aufnahme."));
        Read = Viewport->ReadPixels(Pixels);
    }
    else
    {
        FActorSpawnParameters Parameters;
        Parameters.ObjectFlags = RF_Transient;
        ASceneCapture2D* Actor = World->SpawnActor<ASceneCapture2D>(ASceneCapture2D::StaticClass(), Position, Rotation, Parameters);
        if (!Actor) return Fail(TEXT("unreal.capture_failed"), TEXT("Die temporäre Szenenkamera konnte nicht erstellt werden."));
        UTextureRenderTarget2D* Target = NewObject<UTextureRenderTarget2D>(GetTransientPackage());
        Target->InitAutoFormat(Size.X, Size.Y);
        Target->UpdateResourceImmediate(true);
        USceneCaptureComponent2D* Capture = Actor->GetCaptureComponent2D();
        Capture->TextureTarget = Target;
        Capture->FOVAngle = FieldOfView;
        Capture->bCaptureEveryFrame = false;
        Capture->PostProcessSettings = SelectedCamera->PostProcessSettings;
        Capture->PostProcessBlendWeight = SelectedCamera->PostProcessBlendWeight;
        Capture->CaptureScene();
        FlushRenderingCommands();
        FTextureRenderTargetResource* Resource = Target->GameThread_GetRenderTargetResource();
        Read = Resource && Resource->ReadPixels(Pixels);
        Actor->Destroy();
    }
    if (!Read || Pixels.Num() != Size.X * Size.Y)
        return Fail(TEXT("unreal.capture_failed"), TEXT("Die Szenenkamera lieferte kein lesbares Bild."));
    for (FColor& Pixel : Pixels) Pixel.A = 255;
    TArray64<uint8> Png;
    FImageUtils::PNGCompressImageArray(Size.X, Size.Y, MakeArrayView64(Pixels.GetData(), Pixels.Num()), Png);
    if (Png.IsEmpty() || !FFileHelper::SaveArrayToFile(Png, *Path))
        return Fail(TEXT("unreal.capture_failed"), TEXT("Die PNG-Aufnahme konnte nicht geschrieben werden."));
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetStringField(TEXT("state"), TEXT("completed"));
    Result->SetStringField(TEXT("operationId"), OperationId);
    Result->SetStringField(TEXT("path"), Path);
    Result->SetStringField(TEXT("projectPath"), FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath()));
    Result->SetStringField(TEXT("instanceId"), InstanceId);
    Result->SetStringField(TEXT("world"), World->GetPathName());
    Result->SetStringField(TEXT("source"), Source);
    Result->SetStringField(TEXT("cameraName"), CameraIdentity);
    Result->SetStringField(TEXT("cameraRequest"), CameraName);
    Result->SetObjectField(TEXT("position"), VectorValue(Position));
    Result->SetObjectField(TEXT("rotation"), RotationValue(Rotation));
    Result->SetNumberField(TEXT("fieldOfView"), FieldOfView);
    Result->SetNumberField(TEXT("width"), Size.X);
    Result->SetNumberField(TEXT("height"), Size.Y);
    Result->SetStringField(TEXT("renderMode"), Source == TEXT("viewport") ? TEXT("viewport") : TEXT("sceneCapture"));
    Captures.Add(OperationId, Result);
    TSharedPtr<FJsonObject> Pending = MakeShared<FJsonObject>();
    Pending->SetStringField(TEXT("state"), TEXT("pending"));
    Pending->SetStringField(TEXT("operationId"), OperationId);
    return Success(Pending);
}

FString AssetFile(const FString& Path)
{
    return FPackageName::LongPackageNameToFilename(Path, FPackageName::GetAssetPackageExtension());
}

bool ValidAssetPath(const FString& Path)
{
    return Path.StartsWith(TEXT("/Game/")) && FPackageName::IsValidLongPackageName(Path);
}

UBlueprint* LoadBlueprint(const FString& Path)
{
    if (!ValidAssetPath(Path)) return nullptr;
    const FString ObjectPath = Path + TEXT(".") + FPackageName::GetLongPackageAssetName(Path);
    UBlueprint* Blueprint = LoadObject<UBlueprint>(nullptr, *ObjectPath);
    if (Blueprint && !PackageRevisions.Contains(Path))
        PackageRevisions.Add(Path, FMD5Hash::HashFile(*AssetFile(Path)));
    return Blueprint;
}

UEdGraph* GraphFor(UBlueprint* Blueprint, const FString& Requested)
{
    TArray<UEdGraph*> Graphs;
    Blueprint->GetAllGraphs(Graphs);
    const FString Name = Requested.IsEmpty() ? TEXT("EventGraph") : Requested;
    for (UEdGraph* Graph : Graphs) if (Graph && Graph->GetName() == Name) return Graph;
    return nullptr;
}

UEdGraphNode* NodeFor(UEdGraph* Graph, const FString& Id)
{
    FGuid Guid;
    if (!FGuid::Parse(Id, Guid)) return nullptr;
    for (UEdGraphNode* Node : Graph->Nodes) if (Node && Node->NodeGuid == Guid) return Node;
    return nullptr;
}

TSharedPtr<FJsonObject> ReadBlueprint(UBlueprint* Blueprint, const FString& Path)
{
    TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
    Result->SetStringField(TEXT("assetPath"), Path);
    Result->SetBoolField(TEXT("dirty"), Blueprint->GetOutermost()->IsDirty());
    TArray<TSharedPtr<FJsonValue>> Variables;
    for (const FBPVariableDescription& Variable : Blueprint->NewVariables)
    {
        TSharedPtr<FJsonObject> Value = MakeShared<FJsonObject>();
        Value->SetStringField(TEXT("name"), Variable.VarName.ToString());
        Value->SetStringField(TEXT("type"), Variable.VarType.PinCategory.ToString());
        Value->SetStringField(TEXT("defaultValue"), Variable.DefaultValue);
        Variables.Add(MakeShared<FJsonValueObject>(Value));
    }
    Result->SetArrayField(TEXT("variables"), Variables);
    TArray<TSharedPtr<FJsonValue>> Components;
    if (Blueprint->SimpleConstructionScript)
    {
        for (USCS_Node* Component : Blueprint->SimpleConstructionScript->GetAllNodes())
        {
            if (!Component) continue;
            TSharedPtr<FJsonObject> Value = MakeShared<FJsonObject>();
            Value->SetStringField(TEXT("name"), Component->GetVariableName().ToString());
            Value->SetStringField(TEXT("classPath"), Component->ComponentClass ? Component->ComponentClass->GetPathName() : TEXT(""));
            Components.Add(MakeShared<FJsonValueObject>(Value));
        }
    }
    Result->SetArrayField(TEXT("components"), Components);
    TArray<UEdGraph*> Graphs;
    Blueprint->GetAllGraphs(Graphs);
    TArray<TSharedPtr<FJsonValue>> GraphValues;
    for (UEdGraph* Graph : Graphs)
    {
        if (!Graph) continue;
        TSharedPtr<FJsonObject> GraphValue = MakeShared<FJsonObject>();
        GraphValue->SetStringField(TEXT("name"), Graph->GetName());
        TArray<TSharedPtr<FJsonValue>> Nodes;
        for (UEdGraphNode* Node : Graph->Nodes)
        {
            if (!Node) continue;
            TSharedPtr<FJsonObject> NodeValue = MakeShared<FJsonObject>();
            NodeValue->SetStringField(TEXT("nodeId"), Node->NodeGuid.ToString());
            NodeValue->SetStringField(TEXT("title"), Node->GetNodeTitle(ENodeTitleType::ListView).ToString());
            NodeValue->SetStringField(TEXT("class"), Node->GetClass()->GetPathName());
            TArray<TSharedPtr<FJsonValue>> Pins;
            for (UEdGraphPin* Pin : Node->Pins)
            {
                if (!Pin) continue;
                TSharedPtr<FJsonObject> PinValue = MakeShared<FJsonObject>();
                PinValue->SetStringField(TEXT("name"), Pin->PinName.ToString());
                PinValue->SetStringField(TEXT("direction"), Pin->Direction == EGPD_Input ? TEXT("input") : TEXT("output"));
                PinValue->SetStringField(TEXT("type"), Pin->PinType.PinCategory.ToString());
                PinValue->SetStringField(TEXT("defaultValue"), Pin->DefaultValue);
                TArray<TSharedPtr<FJsonValue>> Links;
                for (UEdGraphPin* Linked : Pin->LinkedTo)
                {
                    if (!Linked || !Linked->GetOwningNode()) continue;
                    TSharedPtr<FJsonObject> Link = MakeShared<FJsonObject>();
                    Link->SetStringField(TEXT("nodeId"), Linked->GetOwningNode()->NodeGuid.ToString());
                    Link->SetStringField(TEXT("pin"), Linked->PinName.ToString());
                    Links.Add(MakeShared<FJsonValueObject>(Link));
                }
                PinValue->SetArrayField(TEXT("links"), Links);
                Pins.Add(MakeShared<FJsonValueObject>(PinValue));
            }
            NodeValue->SetArrayField(TEXT("pins"), Pins);
            Nodes.Add(MakeShared<FJsonValueObject>(NodeValue));
        }
        GraphValue->SetArrayField(TEXT("nodes"), Nodes);
        GraphValues.Add(MakeShared<FJsonValueObject>(GraphValue));
    }
    Result->SetArrayField(TEXT("graphs"), GraphValues);
    return Result;
}
}

static TSharedPtr<FJsonObject> ExecuteCommand(const FString& Command, const TSharedPtr<FJsonObject>& Args, const FString& InstanceId)
{
    check(IsInGameThread());
    if (Command == TEXT("status"))
    {
        TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
        Result->SetStringField(TEXT("instanceId"), InstanceId);
        Result->SetNumberField(TEXT("processId"), FPlatformProcess::GetCurrentProcessId());
        Result->SetStringField(TEXT("projectPath"), FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath()));
        Result->SetStringField(TEXT("engineVersion"), FEngineVersion::Current().ToString());
        UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
        Result->SetStringField(TEXT("world"), World ? World->GetPathName() : TEXT(""));
        TArray<TSharedPtr<FJsonValue>> WorldActors;
        int32 ActorCount = 0;
        if (World)
        {
            for (TActorIterator<AActor> It(World); It; ++It)
            {
                ++ActorCount;
                if (WorldActors.Num() >= 512) continue;
                TSharedPtr<FJsonObject> Value = MakeShared<FJsonObject>();
                Value->SetStringField(TEXT("name"), It->GetName());
                Value->SetStringField(TEXT("classPath"), It->GetClass()->GetPathName());
                WorldActors.Add(MakeShared<FJsonValueObject>(Value));
            }
        }
        Result->SetArrayField(TEXT("worldActors"), WorldActors);
        Result->SetNumberField(TEXT("actorCount"), ActorCount);
        Result->SetBoolField(TEXT("actorsTruncated"), ActorCount > WorldActors.Num());
        TArray<TSharedPtr<FJsonValue>> Cameras;
        if (World)
        {
            for (TActorIterator<ACameraActor> It(World); It; ++It)
            {
                UCameraComponent* Camera = It->GetCameraComponent();
                if (!Camera) continue;
                TSharedPtr<FJsonObject> Value = MakeShared<FJsonObject>();
                Value->SetStringField(TEXT("name"), It->GetName());
                Value->SetStringField(TEXT("label"), It->GetActorLabel());
                Value->SetStringField(TEXT("path"), It->GetPathName());
                Value->SetObjectField(TEXT("position"), VectorValue(Camera->GetComponentLocation()));
                Value->SetObjectField(TEXT("rotation"), RotationValue(Camera->GetComponentRotation()));
                Value->SetNumberField(TEXT("fieldOfView"), Camera->FieldOfView);
                Cameras.Add(MakeShared<FJsonValueObject>(Value));
            }
        }
        Result->SetArrayField(TEXT("cameras"), Cameras);
        TArray<TSharedPtr<FJsonValue>> Capabilities;
        for (const TCHAR* Name : { TEXT("blueprint.read"), TEXT("blueprint.create"), TEXT("blueprint.variable.add"),
            TEXT("blueprint.node.add"), TEXT("blueprint.pin.connect"), TEXT("blueprint.compile"), TEXT("blueprint.save"),
            TEXT("blueprint.component.add"), TEXT("blueprint.component.remove"), TEXT("blueprint.component.set"),
            TEXT("blueprint.variable.remove"), TEXT("blueprint.node.remove"), TEXT("blueprint.pin.disconnect"),
            TEXT("blueprint.graph.create"), TEXT("blueprint.graph.remove"), TEXT("camera.create"),
            TEXT("view.capture"), TEXT("operation.status") })
            Capabilities.Add(MakeShared<FJsonValueString>(Name));
        Result->SetArrayField(TEXT("capabilities"), Capabilities);
        return Success(Result);
    }
    if (Command == TEXT("view.capture")) return CaptureView(Args, InstanceId);
    if (Command == TEXT("camera.create"))
    {
        UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
        if (!World) return Fail(TEXT("unreal.world_missing"), TEXT("Es ist keine Editor-Welt geöffnet."));
        const FString Name = String(Args, TEXT("cameraName"));
        const TSharedPtr<FJsonObject>* PositionValue = nullptr;
        const TSharedPtr<FJsonObject>* RotationObject = nullptr;
        if (Name.IsEmpty() || !Args->TryGetObjectField(TEXT("position"), PositionValue)
            || !Args->TryGetObjectField(TEXT("rotation"), RotationObject))
            return Fail(TEXT("unreal.camera_arguments"), TEXT("Kameraname, Position und Drehung sind erforderlich."));
        double X, Y, Z, Pitch, Yaw, Roll;
        if (!(*PositionValue)->TryGetNumberField(TEXT("x"), X) || !(*PositionValue)->TryGetNumberField(TEXT("y"), Y)
            || !(*PositionValue)->TryGetNumberField(TEXT("z"), Z) || !(*RotationObject)->TryGetNumberField(TEXT("pitch"), Pitch)
            || !(*RotationObject)->TryGetNumberField(TEXT("yaw"), Yaw) || !(*RotationObject)->TryGetNumberField(TEXT("roll"), Roll)
            || !FMath::IsFinite(X) || !FMath::IsFinite(Y) || !FMath::IsFinite(Z)
            || !FMath::IsFinite(Pitch) || !FMath::IsFinite(Yaw) || !FMath::IsFinite(Roll))
            return Fail(TEXT("unreal.camera_arguments"), TEXT("Kameraposition und Drehung müssen gültige Zahlen enthalten."));
        for (TActorIterator<ACameraActor> It(World); It; ++It)
            if (It->GetName() == Name || It->GetActorLabel() == Name)
                return Fail(TEXT("unreal.camera_exists"), TEXT("Eine Kamera mit diesem Namen existiert bereits."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "CreateCamera", "Missum: Kamera erstellen"));
        World->Modify();
        FActorSpawnParameters Parameters;
        Parameters.ObjectFlags = RF_Transactional;
        ACameraActor* Camera = World->SpawnActor<ACameraActor>(ACameraActor::StaticClass(),
            FVector(X, Y, Z), FRotator(Pitch, Yaw, Roll), Parameters);
        if (!Camera) return Fail(TEXT("unreal.camera_failed"), TEXT("Die Kamera konnte nicht erstellt werden."));
        Camera->SetActorLabel(Name);
        Camera->MarkPackageDirty();
        TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
        Result->SetStringField(TEXT("name"), Camera->GetName());
        Result->SetStringField(TEXT("label"), Camera->GetActorLabel());
        Result->SetStringField(TEXT("path"), Camera->GetPathName());
        Result->SetStringField(TEXT("world"), World->GetPathName());
        Result->SetObjectField(TEXT("position"), VectorValue(Camera->GetActorLocation()));
        Result->SetObjectField(TEXT("rotation"), RotationValue(Camera->GetActorRotation()));
        Result->SetBoolField(TEXT("worldDirty"), true);
        Result->SetStringField(TEXT("message"), TEXT("Kamera erstellt; das Level enthält ungespeicherte Änderungen."));
        return Success(Result);
    }
    if (Command == TEXT("operation.status"))
    {
        const TSharedPtr<FJsonObject>* Capture = Captures.Find(String(Args, TEXT("operationId")));
        return Capture ? Success(*Capture) : Fail(TEXT("unreal.operation_missing"), TEXT("Aufnahmeauftrag wurde nicht gefunden."));
    }
    const FString Path = String(Args, TEXT("assetPath"));
    if (!ValidAssetPath(Path)) return Fail(TEXT("unreal.invalid_asset"), TEXT("assetPath muss ein gültiger /Game/-Assetpfad sein."));
    if (Command == TEXT("blueprint.create"))
    {
        if (FPackageName::DoesPackageExist(Path))
            return Fail(TEXT("unreal.asset_exists"), TEXT("Das Asset existiert bereits."));
        UPackage* Package = CreatePackage(*Path);
        const FString Name = FPackageName::GetLongPackageAssetName(Path);
        UBlueprint* Blueprint = FKismetEditorUtilities::CreateBlueprint(AActor::StaticClass(), Package, FName(*Name),
            BPTYPE_Normal, UBlueprint::StaticClass(), UBlueprintGeneratedClass::StaticClass());
        if (!Blueprint) return Fail(TEXT("unreal.create_failed"), TEXT("Der Actor-Blueprint konnte nicht erstellt werden."));
        FAssetRegistryModule::AssetCreated(Blueprint);
        Package->MarkPackageDirty();
        PackageRevisions.Add(Path, FMD5Hash::HashFile(*AssetFile(Path)));
        return Success(ReadBlueprint(Blueprint, Path));
    }
    UBlueprint* Blueprint = LoadBlueprint(Path);
    if (!Blueprint) return Fail(TEXT("unreal.blueprint_missing"), TEXT("Blueprint wurde im Projekt nicht gefunden."));
    if (Command == TEXT("blueprint.read")) return Success(ReadBlueprint(Blueprint, Path));
    if (Command == TEXT("blueprint.component.remove") || Command == TEXT("blueprint.component.set"))
    {
        const FString Name = String(Args, TEXT("name"));
        USCS_Node* Component = Blueprint->SimpleConstructionScript
            ? Blueprint->SimpleConstructionScript->FindSCSNode(FName(*Name)) : nullptr;
        if (!Component) return Fail(TEXT("unreal.component_missing"), TEXT("Komponente wurde nicht gefunden."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "EditComponent", "Missum: Komponente ändern"));
        Blueprint->Modify();
        Blueprint->SimpleConstructionScript->Modify();
        if (Command == TEXT("blueprint.component.remove"))
        {
            Blueprint->SimpleConstructionScript->RemoveNodeAndPromoteChildren(Component);
        }
        else
        {
            UObject* Template = Component->ComponentTemplate;
            FProperty* Property = Template ? FindFProperty<FProperty>(Template->GetClass(), FName(*String(Args, TEXT("property")))) : nullptr;
            if (!Property || !Property->HasAnyPropertyFlags(CPF_Edit) || Property->HasAnyPropertyFlags(CPF_EditConst)
                || !(Property->IsA<FBoolProperty>() || Property->IsA<FIntProperty>() || Property->IsA<FFloatProperty>()
                    || Property->IsA<FDoubleProperty>() || Property->IsA<FStrProperty>() || Property->IsA<FNameProperty>()))
                return Fail(TEXT("unreal.property_invalid"), TEXT("Diese Komponenteneigenschaft wird nicht unterstützt."));
            Template->Modify();
            void* Address = Property->ContainerPtrToValuePtr<void>(Template);
            FString Before;
            Property->ExportTextItem_Direct(Before, Address, nullptr, Template, PPF_None);
            if (!Property->ImportText_Direct(*String(Args, TEXT("value")), Address, Template, PPF_None))
            {
                Property->ImportText_Direct(*Before, Address, Template, PPF_None);
                return Fail(TEXT("unreal.property_value"), TEXT("Wert passt nicht zur Komponenteneigenschaft."));
            }
            Template->PostEditChange();
        }
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.component.add"))
    {
        const FString Name = String(Args, TEXT("name"));
        const FString ClassPath = String(Args, TEXT("classPath"));
        UClass* ComponentClass = LoadObject<UClass>(nullptr, *ClassPath);
        if (Name.IsEmpty() || !ComponentClass || !ComponentClass->IsChildOf(UActorComponent::StaticClass())
            || ComponentClass->HasAnyClassFlags(CLASS_Abstract | CLASS_Deprecated)
            || !Blueprint->SimpleConstructionScript)
            return Fail(TEXT("unreal.component_invalid"), TEXT("Name oder Komponentenklasse ist ungültig."));
        if (Blueprint->SimpleConstructionScript->FindSCSNode(FName(*Name)))
            return Fail(TEXT("unreal.component_exists"), TEXT("Eine Komponente mit diesem Namen existiert bereits."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "AddComponent", "Missum: Komponente hinzufügen"));
        Blueprint->Modify();
        Blueprint->SimpleConstructionScript->Modify();
        USCS_Node* Component = Blueprint->SimpleConstructionScript->CreateNode(ComponentClass, FName(*Name));
        if (!Component) return Fail(TEXT("unreal.component_failed"), TEXT("Komponente konnte nicht erstellt werden."));
        Blueprint->SimpleConstructionScript->AddNode(Component);
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.variable.add"))
    {
        const FString Name = String(Args, TEXT("name"));
        const FString Type = String(Args, TEXT("type"));
        FEdGraphPinType PinType;
        if (Type == TEXT("bool")) PinType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
        else if (Type == TEXT("int")) PinType.PinCategory = UEdGraphSchema_K2::PC_Int;
        else if (Type == TEXT("float")) { PinType.PinCategory = UEdGraphSchema_K2::PC_Real; PinType.PinSubCategory = UEdGraphSchema_K2::PC_Float; }
        else if (Type == TEXT("string")) PinType.PinCategory = UEdGraphSchema_K2::PC_String;
        else if (Type == TEXT("name")) PinType.PinCategory = UEdGraphSchema_K2::PC_Name;
        else if (Type == TEXT("vector")) { PinType.PinCategory = UEdGraphSchema_K2::PC_Struct; PinType.PinSubCategoryObject = TBaseStructure<FVector>::Get(); }
        else return Fail(TEXT("unreal.variable_type"), TEXT("Variablentyp wird nicht unterstützt."));
        if (Name.IsEmpty()) return Fail(TEXT("unreal.variable_name"), TEXT("Variablenname fehlt."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "AddVariable", "Missum: Variable hinzufügen"));
        Blueprint->Modify();
        if (!FBlueprintEditorUtils::AddMemberVariable(Blueprint, FName(*Name), PinType, String(Args, TEXT("defaultValue"))))
            return Fail(TEXT("unreal.variable_failed"), TEXT("Variable konnte nicht erstellt werden."));
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.variable.remove"))
    {
        const FName Name(*String(Args, TEXT("name")));
        if (FBlueprintEditorUtils::FindNewVariableIndex(Blueprint, Name) == INDEX_NONE)
            return Fail(TEXT("unreal.variable_missing"), TEXT("Blueprint-Variable nicht gefunden."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "RemoveVariable", "Missum: Variable entfernen"));
        Blueprint->Modify();
        FBlueprintEditorUtils::RemoveMemberVariable(Blueprint, Name);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.compile"))
    {
        FCompilerResultsLog Log;
        FKismetEditorUtilities::CompileBlueprint(Blueprint, EBlueprintCompileOptions::None, &Log);
        TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
        Result->SetStringField(TEXT("assetPath"), Path);
        Result->SetNumberField(TEXT("errors"), Log.NumErrors);
        Result->SetNumberField(TEXT("warnings"), Log.NumWarnings);
        TArray<TSharedPtr<FJsonValue>> Messages;
        for (const TSharedRef<FTokenizedMessage>& Message : Log.Messages)
            Messages.Add(MakeShared<FJsonValueString>(Message->ToText().ToString()));
        Result->SetArrayField(TEXT("diagnostics"), Messages);
        return Success(Result);
    }
    if (Command == TEXT("blueprint.save"))
    {
        const FString Filename = AssetFile(Path);
        if (FMD5Hash::HashFile(*Filename) != PackageRevisions.FindRef(Path))
            return Fail(TEXT("unreal.save_conflict"), TEXT("Die Assetdatei wurde außerhalb dieser Sitzung geändert. Bitte Zustand erneut prüfen."));
        if (!IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true))
            return Fail(TEXT("unreal.save_failed"), TEXT("Das Assetverzeichnis konnte nicht erstellt werden."));
        FSavePackageArgs SaveArgs;
        SaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
        if (!UPackage::SavePackage(Blueprint->GetOutermost(), Blueprint, *Filename, SaveArgs))
            return Fail(TEXT("unreal.save_failed"), TEXT("Blueprint konnte nicht gespeichert werden."));
        PackageRevisions.Add(Path, FMD5Hash::HashFile(*Filename));
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.graph.create"))
    {
        const FString Name = String(Args, TEXT("graph"));
        if (Name.IsEmpty() || GraphFor(Blueprint, Name) || !FChar::IsAlpha(Name[0]))
            return Fail(TEXT("unreal.graph_invalid"), TEXT("Der Funktionsgraphname fehlt, ist ungültig oder existiert bereits."));
        for (TCHAR Character : Name)
            if (!FChar::IsAlnum(Character) && Character != TEXT('_'))
                return Fail(TEXT("unreal.graph_invalid"), TEXT("Funktionsgraphnamen benötigen Buchstaben, Ziffern oder Unterstriche."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "CreateGraph", "Missum: Funktionsgraph erstellen"));
        Blueprint->Modify();
        UEdGraph* NewGraph = FBlueprintEditorUtils::CreateNewGraph(Blueprint, FName(*Name), UEdGraph::StaticClass(), UEdGraphSchema_K2::StaticClass());
        if (!NewGraph) return Fail(TEXT("unreal.graph_failed"), TEXT("Funktionsgraph konnte nicht erstellt werden."));
        FBlueprintEditorUtils::AddFunctionGraph<UClass>(Blueprint, NewGraph, true, nullptr);
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    UEdGraph* Graph = GraphFor(Blueprint, String(Args, TEXT("graph")));
    if (!Graph) return Fail(TEXT("unreal.graph_missing"), TEXT("Blueprint-Graph wurde nicht gefunden."));
    if (Command == TEXT("blueprint.graph.remove"))
    {
        if (!Blueprint->FunctionGraphs.Contains(Graph))
            return Fail(TEXT("unreal.graph_invalid"), TEXT("Nur eigene Funktionsgraphen können entfernt werden."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "RemoveGraph", "Missum: Funktionsgraph entfernen"));
        Blueprint->Modify();
        FBlueprintEditorUtils::RemoveGraph(Blueprint, Graph);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.pin.connect") || Command == TEXT("blueprint.pin.disconnect"))
    {
        UEdGraphNode* From = NodeFor(Graph, String(Args, TEXT("fromNode")));
        UEdGraphNode* To = NodeFor(Graph, String(Args, TEXT("toNode")));
        UEdGraphPin* Output = From ? From->FindPin(FName(*String(Args, TEXT("fromPin"))), EGPD_Output) : nullptr;
        UEdGraphPin* Input = To ? To->FindPin(FName(*String(Args, TEXT("toPin"))), EGPD_Input) : nullptr;
        if (!Output || !Input) return Fail(TEXT("unreal.pin_missing"), TEXT("Knoten oder Pin wurde nicht gefunden."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "ConnectPins", "Missum: Pins verbinden"));
        Graph->Modify();
        From->Modify();
        To->Modify();
        if (Command == TEXT("blueprint.pin.connect"))
        {
            if (!Graph->GetSchema()->TryCreateConnection(Output, Input))
                return Fail(TEXT("unreal.pin_incompatible"), TEXT("Die Pins können nicht verbunden werden."));
        }
        else
        {
            if (!Output->LinkedTo.Contains(Input))
                return Fail(TEXT("unreal.pin_not_connected"), TEXT("Die Pins sind nicht miteinander verbunden."));
            Output->BreakLinkTo(Input);
        }
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.node.remove"))
    {
        UEdGraphNode* Node = NodeFor(Graph, String(Args, TEXT("nodeId")));
        if (!Node || !Node->CanUserDeleteNode())
            return Fail(TEXT("unreal.node_missing"), TEXT("Der Knoten wurde nicht gefunden oder kann nicht entfernt werden."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "RemoveNode", "Missum: Blueprint-Knoten entfernen"));
        Blueprint->Modify();
        Graph->Modify();
        Node->Modify();
        Node->DestroyNode();
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        return Success(ReadBlueprint(Blueprint, Path));
    }
    if (Command == TEXT("blueprint.node.add"))
    {
        const FString Kind = String(Args, TEXT("kind"));
        const FString Name = String(Args, TEXT("name"));
        if (Name.IsEmpty()) return Fail(TEXT("unreal.node_name"), TEXT("Knotenname fehlt."));
        UEdGraphNode* Node = nullptr;
        if (Kind == TEXT("get") || Kind == TEXT("set"))
        {
            if (FBlueprintEditorUtils::FindNewVariableIndex(Blueprint, FName(*Name)) == INDEX_NONE)
                return Fail(TEXT("unreal.variable_missing"), TEXT("Blueprint-Variable nicht gefunden."));
            if (Kind == TEXT("get"))
            {
                UK2Node_VariableGet* Variable = NewObject<UK2Node_VariableGet>(Graph);
                Variable->VariableReference.SetSelfMember(FName(*Name));
                Node = Variable;
            }
            else
            {
                UK2Node_VariableSet* Variable = NewObject<UK2Node_VariableSet>(Graph);
                Variable->VariableReference.SetSelfMember(FName(*Name));
                Node = Variable;
            }
        }
        else if (Kind == TEXT("event") || Kind == TEXT("call"))
        {
            FString ClassPath = String(Args, TEXT("classPath"));
            if (ClassPath.IsEmpty()) ClassPath = TEXT("/Script/Engine.Actor");
            UClass* OwnerClass = LoadObject<UClass>(nullptr, *ClassPath);
            UFunction* Function = OwnerClass ? OwnerClass->FindFunctionByName(FName(*Name)) : nullptr;
            if (!Function) return Fail(TEXT("unreal.function_missing"), TEXT("Die angegebene Funktion wurde nicht gefunden."));
            if (Kind == TEXT("event"))
            {
                UK2Node_Event* Event = NewObject<UK2Node_Event>(Graph);
                Event->EventReference.SetExternalMember(FName(*Name), OwnerClass);
                Event->bOverrideFunction = true;
                Node = Event;
            }
            else
            {
                UK2Node_CallFunction* Call = NewObject<UK2Node_CallFunction>(Graph);
                Call->SetFromFunction(Function);
                Node = Call;
            }
        }
        else return Fail(TEXT("unreal.node_kind"), TEXT("Knotenart wird nicht unterstützt."));
        const FScopedTransaction Transaction(NSLOCTEXT("Missum", "AddNode", "Missum: Blueprint-Knoten hinzufügen"));
        Blueprint->Modify();
        Graph->Modify();
        double X = 0;
        double Y = 0;
        Args->TryGetNumberField(TEXT("x"), X);
        Args->TryGetNumberField(TEXT("y"), Y);
        Node->SetFlags(RF_Transactional);
        Graph->AddNode(Node, true, false);
        Node->CreateNewGuid();
        Node->NodePosX = static_cast<int32>(X);
        Node->NodePosY = static_cast<int32>(Y);
        Node->PostPlacedNewNode();
        Node->AllocateDefaultPins();
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        TSharedPtr<FJsonObject> Result = MakeShared<FJsonObject>();
        Result->SetStringField(TEXT("assetPath"), Path);
        Result->SetStringField(TEXT("nodeId"), Node->NodeGuid.ToString());
        return Success(Result);
    }
    return Fail(TEXT("unreal.command_unknown"), TEXT("Unbekannter Unreal-Befehl."));
}

TSharedPtr<FJsonObject> MissumExecuteCommand(const FString& Command, const TSharedPtr<FJsonObject>& Args, const FString& InstanceId)
{
    const bool Mutating = Command.StartsWith(TEXT("blueprint.")) && Command != TEXT("blueprint.read")
        || Command == TEXT("camera.create")
        || Command == TEXT("view.capture");
    if (!Mutating) return ExecuteCommand(Command, Args, InstanceId);
    const FString OperationId = String(Args, TEXT("operationId"));
    if (OperationId.IsEmpty()) return Fail(TEXT("unreal.operation_id_required"), TEXT("operationId ist für Änderungen erforderlich."));
    FString Fingerprint;
    TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Fingerprint);
    FJsonSerializer::Serialize(Args.ToSharedRef(), Writer);
    Fingerprint = Command + TEXT(":") + Fingerprint;
    if (const TPair<FString, TSharedPtr<FJsonObject>>* Previous = CompletedOperations.Find(OperationId))
        return Previous->Key == Fingerprint ? Previous->Value
            : Fail(TEXT("unreal.operation_conflict"), TEXT("operationId wurde bereits mit anderen Argumenten verwendet."));
    if (CompletedOperations.Num() >= 4096)
        return Fail(TEXT("unreal.operation_limit"), TEXT("Zu viele Vorgänge in dieser Editor-Sitzung. Bitte Sitzung neu starten."));
    TSharedPtr<FJsonObject> Result = ExecuteCommand(Command, Args, InstanceId);
    CompletedOperations.Add(OperationId, TPair<FString, TSharedPtr<FJsonObject>>(Fingerprint, Result));
    return Result;
}
