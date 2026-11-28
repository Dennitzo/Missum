; Missum Labs – Installer (Inno Setup 6). Built by windows\build-installer.ps1, which passes:
;   AppVersion   release version, e.g. 2.1.0 or 2.1.0-rc1
;   ImageVersion numeric x.y.z (app assembly version = Docker image tag = AiStack folder name)
;   AppDir       the portable build (artifacts\portable\win-x64)
;   StackDir     the AI stack sources (artifacts\aistack\<ImageVersion>)
;   OutputDir    where Missum-Setup-<AppVersion>.exe is written
;
; Per-user installation without administrator rights. The heavy part – building the Docker images
; and downloading worker models – runs afterwards in Missum itself (Einstellungen › Lokaler KI-Stack),
; where it shows progress, can be resumed and is reused for updates.

#ifndef AppVersion
  #error AppVersion must be defined (/DAppVersion=x.y.z)
#endif

[Setup]
AppId={{6B0E2B9C-3F5A-4C1E-9D7B-8A2F4E6C1D35}
AppName=Missum Labs
AppVersion={#AppVersion}
AppVerName=Missum Labs {#AppVersion}
AppPublisher=Missum
AppPublisherURL=https://github.com/Dennitzo/Missum
AppSupportURL=https://github.com/Dennitzo/Missum/issues
AppUpdatesURL=https://github.com/Dennitzo/Missum/releases
DefaultDirName={localappdata}\Programs\Missum
DefaultGroupName=Missum Labs
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Missum-Setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\..\src\Missum.App\Assets\AppLogo.ico
UninstallDisplayIcon={app}\Missum Labs.exe
UninstallDisplayName=Missum Labs
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.Requirements=Voraussetzungen
de.RequirementsDescription=Für den lokalen KI-Stack braucht Missum Docker Desktop und eine NVIDIA-Grafikkarte.
de.InstallDocker=Docker Desktop jetzt mit winget installieren (fragt nach Administratorrechten)
de.LaunchApp=Missum starten und den lokalen KI-Stack einrichten
de.DesktopIcon=Desktop-Verknüpfung erstellen
de.RemoveData=Sollen auch die Missum-KI-Daten (Modelle unter %1) entfernt werden? Docker-Images bleiben erhalten und lassen sich in Docker Desktop löschen.
en.Requirements=Requirements
en.RequirementsDescription=The local AI stack needs Docker Desktop and an NVIDIA graphics card.
en.InstallDocker=Install Docker Desktop now with winget (asks for administrator rights)
en.LaunchApp=Start Missum and set up the local AI stack
en.DesktopIcon=Create a desktop shortcut
en.RemoveData=Also remove the Missum AI data (models in %1)? Docker images stay and can be deleted in Docker Desktop.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StackDir}\*"; DestDir: "{localappdata}\Missum\AiStack\{#ImageVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Missum Labs"; Filename: "{app}\Missum Labs.exe"
Name: "{autodesktop}\Missum Labs"; Filename: "{app}\Missum Labs.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Missum Labs.exe"; Parameters: "--ai-stack-setup"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Missum\AiStack\{#ImageVersion}"

[Code]
var
  RequirementsPage: TOutputMsgMemoWizardPage;
  DockerBox: TNewCheckBox;
  DockerMissing: Boolean;

function CommandSucceeds(const FileName, Params: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function OnPath(const Tool: String): Boolean;
begin
  Result := CommandSucceeds(ExpandConstant('{cmd}'), '/C where ' + Tool + ' >NUL 2>NUL');
end;

function Mark(const Met: Boolean): String;
begin
  if Met then Result := '[OK]   ' else Result := '[FEHLT] ';
end;

procedure InitializeWizard;
var
  Report: String;
  WslReady, GpuReady: Boolean;
  FreeMB, TotalMB: Cardinal;
begin
  DockerMissing := not OnPath('docker') and not FileExists(ExpandConstant('{commonpf64}\Docker\Docker\Docker Desktop.exe'));
  WslReady := CommandSucceeds(ExpandConstant('{sys}\wsl.exe'), '--status');
  GpuReady := OnPath('nvidia-smi') or FileExists(ExpandConstant('{sys}\nvidia-smi.exe'));
  if not GetSpaceOnDisk(ExpandConstant('{localappdata}'), True, FreeMB, TotalMB) then FreeMB := 0;

  Report :=
    Mark(not DockerMissing) + 'Docker Desktop (Linux-Container, WSL2-Backend)' + #13#10 +
    Mark(WslReady) + 'Windows-Subsystem für Linux (WSL2)' + #13#10 +
    Mark(GpuReady) + 'NVIDIA-Grafikkarte mit aktuellem Treiber (nvidia-smi)' + #13#10 +
    Mark(FreeMB >= 60000) + Format('Freier Speicher: %d GB (empfohlen: 60 GB für Images und Modelle)', [FreeMB div 1024]) + #13#10 + #13#10 +
    'Die Docker-Images werden nach der Installation in Missum auf diesem PC gebaut; ' +
    'Docker lädt die dafür nötigen Basis-Images automatisch. Ohne Docker und NVIDIA-GPU ' +
    'startet Missum, der lokale KI-Stack lässt sich dann aber nicht einrichten.';

  RequirementsPage := CreateOutputMsgMemoPage(wpSelectTasks, CustomMessage('Requirements'),
    CustomMessage('RequirementsDescription'), '', Report);
  DockerBox := TNewCheckBox.Create(RequirementsPage);
  DockerBox.Parent := RequirementsPage.Surface;
  DockerBox.Caption := CustomMessage('InstallDocker');
  DockerBox.Width := RequirementsPage.SurfaceWidth;
  DockerBox.Checked := DockerMissing and OnPath('winget');
  DockerBox.Enabled := DockerMissing and OnPath('winget');
  DockerBox.Visible := DockerMissing;
  RequirementsPage.RichEditViewer.Height := RequirementsPage.RichEditViewer.Height - ScaleY(28);
  DockerBox.Top := RequirementsPage.RichEditViewer.Top + RequirementsPage.RichEditViewer.Height + ScaleY(8);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and DockerMissing and DockerBox.Checked then
    { winget shows its own progress and elevates the Docker Desktop installer. }
    Exec(ExpandConstant('{cmd}'), '/C winget install --exact --id Docker.DockerDesktop --source winget --accept-package-agreements --accept-source-agreements',
      '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRoot: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataRoot := ExpandConstant('{commonappdata}\Missum-AI-Stack\Models');
  if DirExists(DataRoot) and not UninstallSilent and
     (MsgBox(FmtMessage(CustomMessage('RemoveData'), [DataRoot]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(DataRoot, True, True, True);
end;
