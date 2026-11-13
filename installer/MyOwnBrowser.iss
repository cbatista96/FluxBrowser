#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{92946DAA-5358-47F0-92D7-7081EC5556D6}
AppName=My Own Browser
AppVersion={#AppVersion}
AppPublisher=My Own Browser
AppPublisherURL=https://github.com/cbatista96/MyOwnBrowser
AppSupportURL=https://github.com/cbatista96/MyOwnBrowser/issues
AppUpdatesURL=https://github.com/cbatista96/MyOwnBrowser/releases
DefaultDirName={localappdata}\Programs\MyOwnBrowser
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\MyOwnBrowser\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MyOwnBrowser.exe
OutputDir=..\artifacts
OutputBaseFilename=MyOwnBrowser-Setup-{#AppVersion}-win-x64
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
Name: "{userprograms}\My Own Browser"; Filename: "{app}\MyOwnBrowser.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\MyOwnBrowser.exe"; Parameters: "--register"; Flags: runhidden
Filename: "{app}\MyOwnBrowser.exe"; Description: "{cm:LaunchProgram,My Own Browser}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\MyOwnBrowser.exe"; Parameters: "--unregister"; Flags: runhidden; RunOnceId: "RemoveBrowserRegistration"

