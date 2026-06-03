#define MyAppName "OpenFinger"
#define MyAppPublisher "TheD0ubleC"
#define MyAppURL "https://github.com/TheD0ubleC/OpenFinger"

#ifndef AppVersion
  #error AppVersion is required.
#endif
#ifndef SourceDir
  #error SourceDir is required.
#endif
#ifndef OutputDir
  #error OutputDir is required.
#endif
#ifndef OutputBase
  #error OutputBase is required.
#endif
#ifndef SetupIconFile
  #define SetupIconFile AddBackslash(SourcePath) + "..\\src\\OpenFinger.Control\\Assets\\OpenFinger.ico"
#endif
#ifndef PackageMode
  #define PackageMode "dotnet"
#endif

#if PackageMode == "self-contained"
  #define PackageFlavorLabel "Self-contained .NET runtime"
  #define PackageModeCommentEn "Includes built-in .NET runtime"
  #define PackageModeCommentZh "已包含 .NET 运行库"
#else
  #define PackageFlavorLabel "Requires installed .NET runtime"
  #define PackageModeCommentEn "Requires installed .NET runtime"
  #define PackageModeCommentZh "需要已安装的 .NET 运行库"
#endif

[Setup]
AppId={{88983141-79E0-4483-BF7C-6D3DDF14DBF9}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL + "/releases"}
DefaultDirName={localappdata}\Programs\OpenFinger
DefaultGroupName=OpenFinger
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
SetupIconFile={#SetupIconFile}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableDirPage=no
ChangesAssociations=no
UninstallDisplayIcon={app}\OpenFinger.Control.exe
AppComments={cm:PackageModeComment}
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a desktop shortcut
chinesesimp.CreateDesktopIcon=创建桌面快捷方式
english.LaunchOpenFinger=Launch OpenFinger
chinesesimp.LaunchOpenFinger=启动 OpenFinger
english.PackageModeComment={#PackageModeCommentEn}
chinesesimp.PackageModeComment={#PackageModeCommentZh}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenFinger"; Filename: "{app}\OpenFinger.Control.exe"
Name: "{autodesktop}\OpenFinger"; Filename: "{app}\OpenFinger.Control.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenFinger.Control.exe"; Description: "{cm:LaunchOpenFinger}"; Flags: nowait postinstall skipifsilent
