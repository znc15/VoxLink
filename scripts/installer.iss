; VoxLink Windows 安装包脚本（Inno Setup 6）。
; 编译示例：
;   iscc.exe /DAppVersion=1.0.0 /DReleaseDir="P:\VRCTranslationBoth\artifacts\release\VoxLink-win-x64" /O"P:\VRCTranslationBoth\artifacts\release" installer.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef ReleaseDir
  #error "ReleaseDir 定义缺失：请传入 /DReleaseDir=<发布目录>"
#endif

#define MyAppName "VoxLink"
#define MyAppExeName "VoxLink.exe"

[Setup]
AppId={{8A2F4E1C-9C4B-4D6A-B3E7-1F5A9C2D8E01}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=VoxLink
AppPublisherURL=https://github.com/znc15/VoxLink
AppSupportURL=https://github.com/znc15/VoxLink/releases
AppUpdatesURL=https://github.com/znc15/VoxLink/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=VoxLink
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Setup-VoxLink-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName} {#AppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#SourcePath}..\src\VoxLink.UI\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Files]
Source: "{#ReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
