; Supervertaler for memoQ - installer
;
; Built with Inno Setup, which is what both memoQ's own PDF Preview tool and the
; Lara plugin use - checked on this machine, where each left the tell-tale
; unins000.exe in its install folder. Following the established pattern rather
; than inventing one means an IT department recognises what it is looking at.
;
; What it places, and why each lands where it does:
;
;   The two plugin assemblies and the prompt editor go into memoQ's Addins
;   folder, because that is the only place memoQ looks. The editor is not an
;   add-in and memoQ never loads it, but the options dialog looks for it beside
;   the plugin, so that is where it lives.
;
;   The preview tool goes into our OWN folder under Program Files, NOT the data
;   folder the development build uses. The data folder is per user and resolves
;   through %APPDATA%, and an elevated install may be running as a different
;   user than the translator - so a per-user path decided during an admin
;   install is a path pointing at the wrong profile. The tool registers itself
;   with memoQ by absolute path at first run, so it is free to live anywhere
;   machine-wide.

#define AppName        "Supervertaler for memoQ"
#define AppPublisher   "Michael Beijer"
#define AppUrl         "https://supervertaler.com"
#define SrcRoot        "..\src"

; Passed in by build-installer.sh, which reads it from the built assembly so the
; installer and the plugin can never disagree about which version this is.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8F3A1C62-5D74-4E28-9B3F-0C7A2E6D5148}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\Supervertaler for memoQ
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Supervertaler.MemoQ.Preview.exe
OutputDir=..\dist
OutputBaseFilename=Supervertaler-for-memoQ-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; The add-in goes under Program Files, so this cannot be a per-user install.
PrivilegesRequired=admin

; memoQ 12 is .NET Framework 4.8 x64, and the add-in loads into memoQ's own
; process, so a 32-bit install would produce an add-in memoQ silently ignores.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Into memoQ's Addins folder. {code:MemoQAddins} resolves at install time.
Source: "{#SrcRoot}\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll";       DestDir: "{code:MemoQAddins}"; Flags: ignoreversion
Source: "{#SrcRoot}\Supervertaler.MemoQ.Terms\bin\Release\Supervertaler.MemoQ.Terms.dll"; DestDir: "{code:MemoQAddins}"; Flags: ignoreversion
Source: "{#SrcRoot}\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe"; DestDir: "{code:MemoQAddins}"; Flags: ignoreversion

; The live document link, in our own folder.
Source: "{#SrcRoot}\Supervertaler.MemoQ.Preview\bin\Release\Supervertaler.MemoQ.Preview.exe";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcRoot}\Supervertaler.MemoQ.Preview\bin\Release\Supervertaler.MemoQ.Preview.exe.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Michael pinned the editor to his Start Menu by hand and wanted customers to
; get that without doing it themselves. The tile takes its icon from the exe's
; own Win32 icon, which already carries the mark, so nothing else is needed.
Name: "{autoprograms}\Supervertaler for memoQ"; Filename: "{code:MemoQAddins}\Supervertaler.PromptEditor.exe"

[Code]
var
  MemoQDir: String;

{ The newest memoQ under Program Files. The folder is stamped with the MAJOR
  version only - 12.4 and 12.5 both install into memoQ-12 - so the highest
  number is the right answer rather than merely a guess. }
function FindMemoQ(): String;
var
  Base, Best: String;
  Rec: TFindRec;
  Num, BestNum: Integer;
begin
  Result := '';
  Best := '';
  BestNum := -1;

  Base := ExpandConstant('{commonpf}') + '\memoQ';
  if not DirExists(Base) then Exit;

  if FindFirst(Base + '\memoQ-*', Rec) then
  begin
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          { "memoQ-12" -> 12. A folder whose name carries no number comes
            back as -1 and is skipped rather than guessed at. }
          Num := StrToIntDef(Copy(Rec.Name, 7, Length(Rec.Name)), -1);
          if (Num > BestNum) and FileExists(Base + '\' + Rec.Name + '\memoQ.exe') then
          begin
            BestNum := Num;
            Best := Base + '\' + Rec.Name;
          end;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;

  Result := Best;
end;

function MemoQAddins(Param: String): String;
begin
  Result := MemoQDir + '\Addins';
end;

{ memoQ holds the add-in open, so copying over it fails with a sharing violation
  that names nothing useful. Said plainly instead, and before anything is
  written. }
function MemoQIsRunning(): Boolean;
var
  Res: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq memoQ.exe" | find /I "memoQ.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, Res) then
    Result := (Res = 0);
end;

function InitializeSetup(): Boolean;
begin
  Result := False;

  MemoQDir := FindMemoQ();
  if MemoQDir = '' then
  begin
    MsgBox('memoQ was not found on this computer.' + #13#10#13#10 +
           'Supervertaler for memoQ installs into memoQ''s own Addins folder, so memoQ has to be ' +
           'installed first. If memoQ is installed somewhere unusual, install it in the normal ' +
           'place or get in touch.', mbCriticalError, MB_OK);
    Exit;
  end;

  if MemoQIsRunning() then
  begin
    MsgBox('memoQ is running.' + #13#10#13#10 +
           'Close memoQ and run this installer again. memoQ keeps the add-in open while it runs, ' +
           'so the files cannot be replaced underneath it.', mbCriticalError, MB_OK);
    Exit;
  end;

  Result := True;
end;

{ The live document link needs memoQ's own PDF Preview tool, because that is
  where the interface it talks to lives - we deliberately ship none of memoQ's
  code. Said at the end rather than blocking the install: everything else works
  without it, and a customer who does not use that feature should not be stopped. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Base: String;
  Found: Boolean;
  Rec: TFindRec;
begin
  if CurStep <> ssPostInstall then Exit;

  Found := False;
  Base := ExpandConstant('{commonpf}') + '\memoQ';

  if FindFirst(Base + '\*Preview*', Rec) then
  begin
    try
      repeat
        if FileExists(Base + '\' + Rec.Name + '\MemoQ.PreviewInterfaces.dll') then Found := True;
      until Found or (not FindNext(Rec));
    finally
      FindClose(Rec);
    end;
  end;

  if not Found then
    MsgBox('Installed.' + #13#10#13#10 +
           'One optional extra: the live document link, which lets the AI see the document you have ' +
           'open, needs memoQ''s own PDF Preview tool. It is a free download from memoQ and is not ' +
           'installed on this computer.' + #13#10#13#10 +
           'Everything else works without it.', mbInformation, MB_OK);
end;
