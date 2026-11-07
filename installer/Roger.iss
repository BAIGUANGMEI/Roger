; Roger Windows x64 installer. All prerequisite executables are bundled unmodified.
#if VER < EncodeVer(6,6,0)
  #error Inno Setup 6.6.0 or newer is required (modern dynamic wizard support).
#endif
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef AppSourceDir
  #define AppSourceDir SourcePath + "..\artifacts\Roger-win-x64"
#endif
#ifndef AppOutputDir
  #define AppOutputDir SourcePath + "..\artifacts"
#endif
#ifndef PawnIoInstaller
  #error Supply the verified original PawnIO_setup.exe through /DPawnIoInstaller.
#endif

[Setup]
AppId={{ECFB83A5-0B93-4F01-89AD-0D54EBA6072F}
AppName=Roger
AppVersion={#AppVersion}
AppVerName=Roger {#AppVersion}
AppPublisher=Roger contributors
AppPublisherURL=https://github.com/BAIGUANGMEI/Roger
AppSupportURL=https://github.com/BAIGUANGMEI/Roger/issues
AppUpdatesURL=https://github.com/BAIGUANGMEI/Roger/releases/latest
AppCopyright=Copyright (c) 2026 Roger contributors
DefaultDirName={autopf}\Roger
DefaultGroupName=Roger
AllowNoIcons=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
WizardStyle=modern dynamic
WizardResizable=yes
WizardSizePercent=110
SetupIconFile=..\src\Roger\Assets\Roger.ico
UninstallDisplayIcon={app}\Roger.exe
LicenseFile=..\LICENSE
InfoBeforeFile=INSTALL-NOTES.txt
OutputDir={#AppOutputDir}
OutputBaseFilename=Roger-Setup-{#AppVersion}-x64
OutputManifestFile=Roger-Setup-{#AppVersion}-x64.manifest.txt
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
VersionInfoDescription=Roger Windows x64 Installer
Compression=lzma2/max
SolidCompression=yes
AppMutex=Local\Roger.Desktop.Instance
SetupMutex=Roger.Desktop.Setup
; Roger flushes history on normal exit; never terminate it from the installer.
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
RestartIfNeededByRun=no
Uninstallable=yes
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Keep this separate installer intact. It is only extracted when a prerequisite is missing.
Source: "{#PawnIoInstaller}"; DestName: "PawnIO_setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\Roger"; Filename: "{app}\Roger.exe"; WorkingDir: "{app}"; AppUserModelID: "BAIGUANGMEI.Roger"
Name: "{autodesktop}\Roger"; Filename: "{app}\Roger.exe"; WorkingDir: "{app}"; AppUserModelID: "BAIGUANGMEI.Roger"; Tasks: desktopicon
Name: "{group}\卸载 Roger"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Roger.exe"; WorkingDir: "{app}"; Description: "启动 Roger"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: CanLaunchRoger

[UninstallRun]
Filename: "{app}\Roger.exe"; Parameters: "--unregister-startup"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveCurrentUserRogerStartup"

[Code]
const
  PawnIoExpectedHash = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032';
  PawnIoRegistryKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
var
  PawnIoPrepared: Boolean;
  PawnIoRestartRequired: Boolean;

function CompatiblePawnIoAtRoot(Root: Integer): Boolean;
var
  VersionString: String;
  InstalledVersion, RequiredVersion: Int64;
begin
  Result := RegQueryStringValue(Root, PawnIoRegistryKey, 'DisplayVersion', VersionString);
  if Result then
    Result := StrToVersion(VersionString, InstalledVersion) and
      StrToVersion('2.2.0.0', RequiredVersion);
  if Result then
    Result := (ComparePackedVersion(InstalledVersion, RequiredVersion) >= 0) and
      RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function CompatiblePawnIoInstalled: Boolean;
begin
  Result := CompatiblePawnIoAtRoot(HKLM64) or CompatiblePawnIoAtRoot(HKLM32);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  ExitCode: Integer;
begin
  Result := '';
  if PawnIoPrepared then Exit;
  if CompatiblePawnIoInstalled then
  begin
    Log('PawnIO 2.2.0 or newer is already installed; prerequisite installation skipped.');
    PawnIoPrepared := True;
    Exit;
  end;
  try
    WizardForm.StatusLabel.Caption := '正在安装硬件传感器依赖 PawnIO…';
    ExtractTemporaryFile('PawnIO_setup.exe');
    InstallerPath := ExpandConstant('{tmp}\PawnIO_setup.exe');
    if CompareText(GetSHA256OfFile(InstallerPath), PawnIoExpectedHash) <> 0 then
    begin
      Result := 'PawnIO 依赖文件校验失败，未执行安装。请重新下载 Roger 安装器。';
      Exit;
    end;
    if not Exec(InstallerPath, '-install -silent', ExpandConstant('{tmp}'), SW_HIDE,
      ewWaitUntilTerminated, ExitCode) then
    begin
      Result := Format('无法启动 PawnIO 依赖安装程序（Windows 错误 %d）。', [ExitCode]);
      Exit;
    end;
    Log(Format('Official PawnIO prerequisite installer returned %d.', [ExitCode]));
    if (ExitCode = 3010) or (ExitCode = 1641) then
    begin
      PawnIoRestartRequired := True;
      PawnIoPrepared := True;
      Log('PawnIO requested a restart; Roger Setup will not restart Windows automatically.');
      Exit;
    end;
    if ExitCode <> 0 then
    begin
      Result := Format('PawnIO 依赖安装未完成（退出代码 %d）。请检查 Windows 安装状态后重试；未继续安装 Roger。', [ExitCode]);
      Exit;
    end;
    if not CompatiblePawnIoInstalled then
    begin
      Result := 'PawnIO 安装程序已结束，但未检测到有效的安装信息和驱动服务。请检查 Windows 安装状态后重试。';
      Exit;
    end;
    PawnIoPrepared := True;
  except
    Result := 'PawnIO 依赖安装未完成：' + GetExceptionMessage;
  end;
end;

function CanLaunchRoger: Boolean;
begin
  Result := not PawnIoRestartRequired;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and PawnIoRestartRequired then
    WizardForm.FinishedLabel.Caption := 'Roger 已安装。PawnIO 传感器驱动需要重启 Windows 后生效。请保存工作后手动重启电脑，再打开 Roger。本安装器不会自动重启。';
end;

function GetCustomSetupExitCode: Integer;
begin
  if PawnIoRestartRequired then Result := 3010 else Result := 0;
end;
