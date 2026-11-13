#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{92946DAA-5358-47F0-92D7-7081EC5556D6}
AppName=Flux Browser
AppVersion={#AppVersion}
AppPublisher=World Marketing Labs
AppPublisherURL=https://www.worldmarketinglabs.com/es
AppSupportURL=https://github.com/cbatista96/FluxBrowser/issues
AppUpdatesURL=https://github.com/cbatista96/FluxBrowser/releases
DefaultDirName={localappdata}\Programs\MyOwnBrowser
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\MyOwnBrowser\Assets\AppIcon.ico
UninstallDisplayIcon={app}\FluxBrowser.exe
OutputDir=..\artifacts
OutputBaseFilename=FluxBrowser-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "install.ps1,uninstall.ps1,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Flux Browser"; Filename: "{app}\FluxBrowser.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\FluxBrowser.exe"; Parameters: "--register"; Flags: runhidden
Filename: "{app}\FluxBrowser.exe"; Description: "{cm:LaunchProgram,Flux Browser}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\FluxBrowser.exe"; Parameters: "--unregister"; Flags: runhidden; RunOnceId: "RemoveBrowserRegistration"

