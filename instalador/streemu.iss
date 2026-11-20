; Instalador do Streemu (Inno Setup 6). Quem gera é o scripts/build-release.ps1, que passa:
;   /DVersao=0.2.5  /DOrigem=<pasta dist\Streemu-X.Y.Z pronta>  /DSaida=<pasta dist>
; Instala por usuário (sem administrador) em %LOCALAPPDATA%\Programs\Streemu: a pasta precisa ser gravável para a
; atualização automática (spec 004) trocar o exe.
; O estado (%LOCALAPPDATA%\Streemu) não é tocado.
; O instalador não leva grupo.json (spec 022): o app tem o endereço do servidor embutido e não usa senha.
; No fim da instalação o instalador roda "Streemu.exe --atualizar" (sem janela): o app traz a última versão da main para a pasta instalada
; (spec 004, seção 7). Por isso um instalador antigo também instala a versão mais nova; sem internet isso falha em silêncio e o app
; se atualiza sozinho na primeira abertura.
; Arte do assistente: a Mumu jogando, reduzida por instalador/arte/gerar-arte.ps1 (tamanhos por DPI explicados lá).

#ifndef Versao
  #define Versao "0.0.0"
#endif
#ifndef Origem
  #error Passe /DOrigem=<pasta da release> (o build-release.ps1 faz isso)
#endif
#ifndef Saida
  #define Saida "..\dist"
#endif

#define Nome "Streemu"
#define Exe "Streemu.exe"
; mesmo valor que o app grava em "Iniciar com o Windows" (Util/StartWithWindows.cs): app e instalador veem a mesma escolha
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValor "Streemu"
#define DiscordAppId "1568336028157218906"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{4A4DCC89-4B0D-462F-88DB-30FDC59451D3}_is1"

[Setup]
; AppId fixo do Streemu: é ele que faz um instalador novo atualizar a instalação existente em vez de criar outra
AppId={{4A4DCC89-4B0D-462F-88DB-30FDC59451D3}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
AppPublisher={#Nome} (grupo de amigos)
DefaultDirName={localappdata}\Programs\{#Nome}
; ao atualizar, o Inno reaproveita a pasta da instalação anterior
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; O Streemu precisa do Windows 10 2004 (build 19041) ou mais novo, 64 bits. Quem confere é o InitializeSetup (no [Code]), e não
; MinVersion/ArchitecturesAllowed: com eles o Inno só mostra "não suporta a versão do Windows" e fecha, sem dizer o que fazer e sem
; nada para a pessoa mandar ao suporte. O Setup é de 32 bits, então abre até num Windows de 32 bits e consegue montar o relatório.
ArchitecturesInstallIn64BitMode=x64compatible
; log em %TEMP% ("Setup Log AAAA-MM-DD #NNN.txt"): o relatório de falha diz onde ele está e traz o fim dele
SetupLogging=yes
OutputDir={#Saida}
OutputBaseFilename={#Nome}-{#Versao}-instalador
SetupIconFile=..\app\Streemu\streemu.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#Nome}
Compression=lzma2/ultra64
SolidCompression=yes
#if Ver >= EncodeVer(6, 6, 0)
; claro ou escuro conforme o Windows (o app é escuro; quem usa o Windows escuro vê o instalador escuro também)
WizardStyle=modern dynamic
#else
WizardStyle=modern
#endif
#if Ver >= EncodeVer(6, 5, 2)
; a Mumu jogando nas páginas de boas-vindas e de fim, e o rosto dela no canto das outras páginas. PNG com transparência
; (Inno Setup 6.5.2 ou mais novo); o Setup escolhe o tamanho que combina com o DPI do PC.
WizardImageFile=arte\mumu-lateral-202.png,arte\mumu-lateral-269.png,arte\mumu-lateral-336.png,arte\mumu-lateral-430.png,arte\mumu-lateral-534.png
WizardSmallImageFile=arte\mumu-canto-58.png,arte\mumu-canto-77.png,arte\mumu-canto-97.png,arte\mumu-canto-124.png,arte\mumu-canto-159.png
; lilás bem claro atrás da Mumu (o AccentSoft do app, clareado); no escuro, o fundo da janela do app (Theme.Surface)
WizardImageBackColor=#EEE9FC
#if Ver >= EncodeVer(6, 6, 0)
WizardImageBackColorDynamicDark=#2A2640
#endif
#else
  #pragma message "Inno Setup anterior ao 6.5.2: o instalador sai sem a Mumu (PNG). Atualize com: winget upgrade JRSoftware.InnoSetup"
#endif
; fecha o app (e o ffmpeg/ffplay dele) antes de trocar os arquivos; o app sai de verdade no fim de sessão
CloseApplications=yes
CloseApplicationsFilter={#Exe},ffmpeg.exe,ffplay.exe
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
WelcomeLabel2=Isto instala o [name/ver] neste PC, só para o seu usuário (não precisa de administrador).%n%nA Mumu já pegou o controle. Clique em Avançar.

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "inicio"; Description: "Iniciar o {#Nome} com o Windows (fica na bandeja, perto do relógio)"; GroupDescription: "Ao ligar o PC:"
Name: "firewall"; Description: "Liberar o {#Nome} no Firewall do Windows (o Windows pede permissão de administrador uma vez)"; GroupDescription: "Rede:"

[Files]
Source: "{#Origem}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#Nome}"; Filename: "{app}\{#Exe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#Exe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValor}"; ValueData: """{app}\{#Exe}"" --inicio"; Tasks: inicio; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#Exe}"; Parameters: "--atualizar"; StatusMsg: "Trazendo a versão mais nova do {#Nome} (sem internet, ele se atualiza sozinho ao abrir)..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#Exe}"; Description: "Abrir o {#Nome} agora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; o que o app cria ao lado do exe (relay-check.txt, discord-check.txt, exe antigo da atualização)
Type: filesandordirs; Name: "{app}"

[Code]
{ Na desinstalação, apaga do registro o que o próprio app grava (Run, streemu://, discord-<id>://), mas só quando
  aponta para ESTA pasta: um Streemu de zip em outra pasta continua funcionando. }
function PointsHere(const Value: String): Boolean;
begin
  Result := Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0;
end;

procedure CleanKey(const Key: String);
var
  Cmd: String;
begin
  if RegQueryStringValue(HKCU, Key + '\shell\open\command', '', Cmd) and PointsHere(Cmd) then
    RegDeleteKeyIncludingSubkeys(HKCU, Key);
end;

{ Mesmo comando do app (FirewallCheck.AllowThisApp): apaga as regras antigas deste exe (bloqueio vence permissão) e cria
  a de entrada. Sem isso, a primeira abertura da pasta instalada mostrava o aviso do Windows e o app acusava bloqueio.
  Recusar o UAC não é erro: o app oferece liberar depois pelo botão "Firewall do Windows...".
  A regra se chama Streemu, o mesmo nome que o app cria e procura (`FirewallCheck`). }
procedure AllowFirewall();
var
  Exe, Params: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{app}\{#Exe}');
  Params := '/s /c "netsh advfirewall firewall delete rule name=all program="' + Exe + '" & ' +
            'netsh advfirewall firewall add rule name="Streemu" dir=in action=allow program="' + Exe + '" enable=yes profile=any"';
  if ShellExec('runas', ExpandConstant('{sys}\cmd.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Firewall: liberado (codigo ' + IntToStr(Code) + ')')
  else
    Log('Firewall: nao liberado (' + SysErrorMessage(Code) + ')');
end;

{ ---------- Relatório de falha ----------
  Quando o PC não serve (Windows anterior ao 10 2004, ou sem x64) ou a instalação para no meio, o instalador mostra o que
  aconteceu e um relatório pronto para a pessoa mandar ao suporte ou ao amigo que passou o Streemu: botão Copiar (cola no
  WhatsApp/Discord) e Salvar (arquivo .txt na Área de Trabalho). Nada sai do PC sozinho. O relatório não leva o nome do
  usuário do Windows (trocado por <usuario> nos caminhos), seguindo o que o app promete no README.
  Em modo silencioso (/SILENT, /VERYSILENT) a janela não aparece: o relatório vai só para o log. }
const
  BuildMinimo = 19041;
  WM_COPY = $0301;
  EM_SETSEL = $00B1;
  LinhasDoLog = 40;

var
  RelatorioMemo: TNewMemo;
  BotaoCopiar, BotaoSalvar: TNewButton;
  Instalando, Concluido, Cancelado: Boolean;

function RaizWinNT(): Integer;
begin
  if IsWin64 then Result := HKLM64 else Result := HKLM;
end;

function LerWinNT(const Nome: String): String;
begin
  if not RegQueryStringValue(RaizWinNT(), 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', Nome, Result) then
    Result := '';
end;

function DescreverWindows(): String;
var
  V: TWindowsVersion;
  Ubr: Cardinal;
  Produto, Versao, UbrTxt: String;
begin
  GetWindowsVersionEx(V);
  Produto := LerWinNT('ProductName');
  if Produto = '' then Produto := 'Windows';
  { o registro continua dizendo "Windows 10" no 11 (mesma correção do app em Telemetria/Perfil.cs) }
  if V.Build >= 22000 then StringChangeEx(Produto, 'Windows 10', 'Windows 11', True);
  { DisplayVersion ("22H2") só existe a partir do 20H2; antes o nome era ReleaseId ("1909") }
  Versao := LerWinNT('DisplayVersion');
  if Versao = '' then Versao := LerWinNT('ReleaseId');
  UbrTxt := '';
  if RegQueryDWordValue(RaizWinNT(), 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'UBR', Ubr) then
    UbrTxt := '.' + IntToStr(Ubr);
  Result := Produto;
  if Versao <> '' then Result := Result + ' ' + Versao;
  Result := Result + ' (build ' + IntToStr(V.Major) + '.' + IntToStr(V.Minor) + '.' + IntToStr(V.Build) + UbrTxt + ')';
  if V.ServicePackMajor > 0 then Result := Result + ' SP' + IntToStr(V.ServicePackMajor);
end;

function DescreverSistema(): String;
begin
  case ProcessorArchitecture of
    paX86: Result := '32 bits (x86)';
    paX64: Result := '64 bits (x64)';
    paArm64: Result := 'ARM64';
  else
    Result := 'arquitetura desconhecida';
  end;
  if IsX64Compatible then Result := Result + ', roda programas x64'
  else Result := Result + ', não roda programas x64';
end;

function SemUsuario(const Texto: String): String;
var
  U: String;
begin
  Result := Texto;
  U := GetUserNameString;
  if U <> '' then StringChangeEx(Result, '\' + U + '\', '\<usuario>\', True);
end;

function FimDoLog(): String;
var
  Linhas: TArrayOfString;
  I, De: Integer;
  Arq: String;
begin
  Result := '';
  Arq := ExpandConstant('{log}');
  if (Arq = '') or not LoadStringsFromLockedFile(Arq, Linhas) then
  begin
    Result := '(não consegui ler o log)' + #13#10;
    exit;
  end;
  De := GetArrayLength(Linhas) - LinhasDoLog;
  if De < 0 then De := 0;
  for I := De to GetArrayLength(Linhas) - 1 do
    Result := Result + Linhas[I] + #13#10;
end;

function MontarRelatorio(const Motivo: String; const ComLog: Boolean): String;
var
  NL: String;
begin
  NL := #13#10;
  Result := '--- Streemu: a instalação não deu certo ---' + NL +
    'Motivo: ' + Motivo + NL +
    'Instalador: {#Nome} {#Versao}' + NL +
    'Windows: ' + DescreverWindows() + NL +
    'Sistema: ' + DescreverSistema() + NL +
    'Idioma do Windows: ' + Format('$%.4x', [GetUILanguage]) + NL +
    'Quando: ' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + NL +
    'Log da instalação: ' + ExpandConstant('{log}') + NL;
  if ComLog then
    Result := Result + NL + 'Fim do log:' + NL + FimDoLog();
  Result := SemUsuario(Result);
end;

procedure CopiarClick(Sender: TObject);
begin
  { WM_COPY na caixa de texto: copia como Unicode (acentos certos), sem depender de clip.exe nem do PowerShell }
  SendMessage(RelatorioMemo.Handle, EM_SETSEL, 0, -1);
  SendMessage(RelatorioMemo.Handle, WM_COPY, 0, 0);
  BotaoCopiar.Caption := 'Copiado!';
end;

function ArquivoDoRelatorio(): String;
begin
#ifdef RelatorioEm
  Result := '{#RelatorioEm}';
#else
  Result := ExpandConstant('{userdesktop}\Streemu - erro na instalação.txt');
#endif
end;

procedure SalvarClick(Sender: TObject);
var
  Linhas: TArrayOfString;
begin
  SetArrayLength(Linhas, 1);
  Linhas[0] := RelatorioMemo.Text;
  if SaveStringsToUTF8File(ArquivoDoRelatorio(), Linhas, False) then
    BotaoSalvar.Caption := 'Salvo na Área de Trabalho'
  else
    MsgBox('Não consegui salvar o arquivo. Use o botão Copiar.', mbError, MB_OK);
end;

procedure MostrarRelatorio(const Titulo, Explicacao, Motivo: String; const ComLog: Boolean);
var
  Form: TSetupForm;
  Texto: TNewStaticText;
  Fechar: TNewButton;
  Relatorio: String;
  M: Integer;
begin
  Relatorio := MontarRelatorio(Motivo, ComLog);
  Log('Relatório de falha:' + #13#10 + Relatorio);
  if WizardSilent then exit;

  M := ScaleX(12);
  Form := CreateCustomForm(ScaleX(560), ScaleY(400), False, False);
  try
    Form.Caption := Titulo;

    Texto := TNewStaticText.Create(Form);
    Texto.Parent := Form;
    Texto.Left := M;
    Texto.Top := M;
    Texto.Width := Form.ClientWidth - 2 * M;
    Texto.AutoSize := False;
    Texto.WordWrap := True;
    Texto.Height := ScaleY(110);
    Texto.Caption := Explicacao + #13#10#13#10 +
      'Se precisar de ajuda, mande o relatório abaixo para o suporte do {#Nome} ou para o amigo que te passou o ' +
      '{#Nome}: clique em Copiar e cole na conversa, ou em Salvar para gerar um arquivo na Área de Trabalho.';

    RelatorioMemo := TNewMemo.Create(Form);
    RelatorioMemo.Parent := Form;
    RelatorioMemo.Left := M;
    RelatorioMemo.Top := Texto.Top + Texto.Height + M;
    RelatorioMemo.Width := Form.ClientWidth - 2 * M;
    RelatorioMemo.Height := Form.ClientHeight - RelatorioMemo.Top - ScaleY(23) - 2 * M;
    RelatorioMemo.ReadOnly := True;
    RelatorioMemo.ScrollBars := ssVertical;
    RelatorioMemo.WordWrap := False;
    RelatorioMemo.Text := Relatorio;

    BotaoCopiar := TNewButton.Create(Form);
    BotaoCopiar.Parent := Form;
    BotaoCopiar.Left := M;
    BotaoCopiar.Top := Form.ClientHeight - ScaleY(23) - M;
    BotaoCopiar.Width := ScaleX(110);
    BotaoCopiar.Height := ScaleY(23);
    BotaoCopiar.Caption := 'Copiar';
    BotaoCopiar.OnClick := @CopiarClick;

    BotaoSalvar := TNewButton.Create(Form);
    BotaoSalvar.Parent := Form;
    BotaoSalvar.Left := BotaoCopiar.Left + BotaoCopiar.Width + ScaleX(8);
    BotaoSalvar.Top := BotaoCopiar.Top;
    BotaoSalvar.Width := ScaleX(170);
    BotaoSalvar.Height := ScaleY(23);
    BotaoSalvar.Caption := 'Salvar';
    BotaoSalvar.OnClick := @SalvarClick;

    Fechar := TNewButton.Create(Form);
    Fechar.Parent := Form;
    Fechar.Width := ScaleX(90);
    Fechar.Height := ScaleY(23);
    Fechar.Left := Form.ClientWidth - Fechar.Width - M;
    Fechar.Top := BotaoCopiar.Top;
    Fechar.Caption := 'Fechar';
    Fechar.ModalResult := mrCancel;
    Fechar.Cancel := True;

    Form.ActiveControl := BotaoCopiar;
#ifdef TesteRelatorio
    { teste automático (scripts de teste, nunca na release): copia e salva sem clique }
    CopiarClick(nil);
    SalvarClick(nil);
#endif
    Form.ShowModal();
  finally
    Form.Free();
  end;
end;

function InitializeSetup(): Boolean;
var
  V: TWindowsVersion;
  Explicacao, Motivo: String;
  VersaoVelha, Sem64: Boolean;
begin
  Result := True;
  GetWindowsVersionEx(V);
  VersaoVelha := (V.Major < 10) or ((V.Major = 10) and (V.Build < BuildMinimo));
  Sem64 := not IsX64Compatible;
#ifdef TesteRelatorio
  VersaoVelha := True;
#endif
  if not (VersaoVelha or Sem64) then exit;

  Explicacao := '';
  Motivo := '';
  if VersaoVelha then
  begin
    Explicacao := 'O {#Nome} precisa do Windows 10 versão 2004 (maio de 2020) ou mais nova, ou do Windows 11. ' +
      'Este PC está com o ' + DescreverWindows() + '. ';
    if V.Major < 10 then
      Explicacao := Explicacao + 'Para usar o {#Nome}, o PC precisa passar para o Windows 10 ou 11.'
    else
      Explicacao := Explicacao + 'Atualize pelo Windows Update (Configurações > Atualização e Segurança > Windows Update) e abra o instalador de novo.';
    Motivo := 'Windows antigo demais (precisa do build ' + IntToStr(BuildMinimo) + ' ou mais novo)';
  end;
  if Sem64 then
  begin
    if Explicacao <> '' then Explicacao := Explicacao + #13#10#13#10;
    if ProcessorArchitecture = paArm64 then
      Explicacao := Explicacao + 'Este é um Windows 10 em processador ARM, que não roda programas de 64 bits (x64) como o {#Nome}. No Windows 11 ele funciona.'
    else
      Explicacao := Explicacao + 'O {#Nome} só funciona no Windows de 64 bits, e este Windows é de 32 bits. Para conferir: Configurações > Sistema > Sobre > Tipo de sistema.';
    if Motivo <> '' then Motivo := Motivo + '; ';
    Motivo := Motivo + 'Windows sem suporte a programas x64';
  end;
  MostrarRelatorio('{#Nome}: este PC não consegue instalar', Explicacao, Motivo, False);
  Result := False;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  { quem cancela não teve falha: não mostra relatório no fim }
  Cancelado := True;
end;

procedure DeinitializeSetup();
begin
  if Instalando and not Concluido and not Cancelado then
    MostrarRelatorio('{#Nome}: a instalação parou no meio',
      'A instalação do {#Nome} não terminou e o que foi copiado foi desfeito. Costuma ser antivírus bloqueando a gravação ' +
      'de um arquivo, disco cheio, ou o {#Nome} aberto em outra conta deste PC. Tente de novo; se continuar, mande o relatório.',
      'A instalação parou no meio', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then Instalando := True;
  if CurStep = ssPostInstall then
  begin
    Concluido := True;
    if WizardIsTaskSelected('firewall') then
      AllowFirewall();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#RunValor}', Cmd) and PointsHere(Cmd) then
      RegDeleteValue(HKCU, '{#RunKey}', '{#RunValor}');
    CleanKey('Software\Classes\Streemu');
    CleanKey('Software\Classes\discord-{#DiscordAppId}');
  end;
end;
