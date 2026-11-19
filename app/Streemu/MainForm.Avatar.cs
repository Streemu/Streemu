using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// "Meu avatar" no card do Grupo: escolher uma imagem do PC (JPEG, PNG, GIF, BMP), ver a prévia redonda e remover.
/// Fica fora do MainForm.Grupo.cs para dar menos conflito. Plano 005, T3: por enquanto o avatar aparece só para mim;
/// a T5 manda a miniatura aos amigos e a T6 desenha na faixa do player.
/// </summary>
public sealed partial class MainForm
{
    AvatarStore? _myAvatar;
    AvatarBadge? _myAvatarBadge;
    DiscordAvatarSource? _discordAvatar;
    readonly AvatarCache _avatarCache = new();
    readonly Button _chooseAvatar = new() { Text = "Meu avatar...", AutoSize = true };
    readonly Button _removeAvatar = new() { Text = "Remover", AutoSize = true, Enabled = false };
    readonly CheckBox _useDiscordAvatar = new Ui.ToggleSwitch { Text = "Usar meu avatar do Discord", AutoSize = true };

    /// <summary>A linha do card do Grupo: prévia redonda, "Meu avatar..." e "Remover". Testes automáticos não gravam nada no disco.</summary>
    Control BuildAvatarOptions()
    {
        _myAvatar = new AvatarStore(Automated || !AppState.Persists ? null : AvatarStore.DefaultDirectory);
        _myAvatarBadge = new AvatarBadge(() => Nick) { Size = new Size(36, 36), Margin = new Padding(10, 2, 6, 2) };
        _discordAvatar = new DiscordAvatarSource(new HttpAvatarDownloader(), _avatarCache);
        _friendAvatars = new FriendAvatars(new HttpAvatarDownloader(), _avatarCache);
        _friendAvatars.Changed += () => Ui(RefreshMembers); // o avatar do amigo chegou: o card redesenha a linha dele
        _myAvatar.Changed += ShowMyAvatar;
        _discordAvatar.Changed += () => Ui(ShowMyAvatar); // o download termina em thread de fundo
        _myAvatar.LoadSaved();
        _useDiscordAvatar.Checked = _state.UsarAvatarDiscord;
        _useDiscordAvatar.CheckedChanged += (_, _) => OnUseDiscordAvatarChanged();
        // A foto do Discord da última sessão já vale na abertura, sem esperar o Discord conectar (antes aparecia a inicial).
        if (_useDiscordAvatar.Checked && !Automated) _ = _discordAvatar.SetAccountAsync(_state.DiscordId, _state.DiscordAvatar);
        // Avisos que chegam antes de a janela existir (abertura escondida na bandeja, Discord rápido) são descartados pelo Ui():
        // quando a janela nasce, confere de novo a conta do Discord e a foto.
        HandleCreated += (_, _) => { RefreshDiscordAccount(); ShowMyAvatar(); };
        ShowMyAvatar();
        _chooseAvatar.Click += (_, _) => ChooseAvatarFile();
        _removeAvatar.Click += (_, _) => _myAvatar.Clear();
        _nick.TextChanged += (_, _) => _myAvatarBadge.Invalidate(); // sem imagem, a prévia mostra a inicial do apelido
        ApplyTestAvatar();
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(new Control[] { _myAvatarBadge, _chooseAvatar, _removeAvatar });
        return row;
    }

    /// <summary>Ordem do avatar (spec 005): imagem escolhida no PC, depois o avatar do Discord (se a caixa está ligada), depois a inicial colorida.</summary>
    void ShowMyAvatar()
    {
        _myAvatarBadge!.Image = MyAvatarImage();
        _removeAvatar.Enabled = _myAvatar!.HasAvatar;
        AnnounceAvatar(); // qualquer mudança do meu avatar (imagem, Discord, caixa) chega aos amigos
    }

    /// <summary>A imagem que me representa agora (a mesma ordem do envio aos amigos).</summary>
    AvatarImage? MyAvatarImage() => _myAvatar?.Image ?? (_useDiscordAvatar.Checked ? _discordAvatar?.Image : null);

    void OnUseDiscordAvatarChanged()
    {
        _state.UsarAvatarDiscord = _useDiscordAvatar.Checked;
        if (!Automated) _state.Save();
        ShowMyAvatar();
        if (_useDiscordAvatar.Checked) RefreshDiscordAccount();
    }

    /// <summary>Chamado quando o Discord é criado (MainForm.Grupo.cs): acompanha a conta conectada para pegar o avatar dela.</summary>
    void WatchDiscordAvatar()
    {
        _discord!.Changed += () => Ui(RefreshDiscordAccount);
        RefreshDiscordAccount();
    }

    void RefreshDiscordAccount()
    {
        if (_discord is not { Connected: true } discord) return;
        RememberDiscordAccount(discord);
        if (!_nickManual && FitNick(discord.DiscordUser) is string name && _nick.Text != name) SetNickQuietly(name);
        if (_useDiscordAvatar.Checked) _ = _discordAvatar!.SetAccountAsync(discord.DiscordUserId, discord.DiscordAvatarCode);
    }

    void RememberDiscordAccount(Discord.DiscordPresence d)
    {
        if (Automated || (_state.DiscordNome == d.DiscordUser && _state.DiscordId == d.DiscordUserId && _state.DiscordAvatar == d.DiscordAvatarCode)) return;
        _state.DiscordNome = d.DiscordUser;
        _state.DiscordId = d.DiscordUserId;
        _state.DiscordAvatar = d.DiscordAvatarCode;
        _state.Save();
    }

    // ------------------------------------------------------------------ apelido
    // Apelido digitado vale em toda abertura; sem ele, vale o nome do Discord (o da última sessão até o Discord conectar).
    // Antes o app abria com o nome do usuário do Windows e o nome do Discord nunca chegava ao apelido.

    bool _nickManual, _settingNick;

    /// <summary>Chamado no fim do construtor (MainForm.cs), depois de o Discord ser criado.</summary>
    void InitNick()
    {
        // estado antigo (sem NickManual): um apelido salvo diferente do nome do Windows foi digitado pela pessoa
        _nickManual = (_state.NickManual ?? _state.Nick != Environment.UserName) && !string.IsNullOrWhiteSpace(_state.Nick);
        SetNickQuietly(_nickManual ? _state.Nick!.Trim() : AutoNick());
        _nick.TextChanged += (_, _) => { if (!_settingNick) _nickManual = _nick.Text.Trim().Length > 0; };
        _nick.Leave += (_, _) =>
        {
            if (!_nickManual) SetNickQuietly(AutoNick()); // apagou o apelido: volta ao nome do Discord
            SaveNick();
        };
    }

    void SaveNick()
    {
        if (Automated) return;
        _state.Nick = _nick.Text.Trim();
        _state.NickManual = _nickManual;
        _state.Save();
    }

    string AutoNick() => FitNick(_discord is { Connected: true } d ? d.DiscordUser : null) ?? FitNick(_state.DiscordNome) ?? FitNick(Environment.UserName) ?? "";

    static string? FitNick(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= 24 ? s.Trim() : s.Trim()[..24];

    void SetNickQuietly(string text)
    {
        _settingNick = true;
        try { _nick.Text = text; } finally { _settingNick = false; }
    }

    void ChooseAvatarFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Escolher meu avatar", CheckFileExists = true,
            Filter = $"Imagens ({AvatarLimits.AcceptedFormats})|*.jpg;*.jpeg;*.png;*.gif;*.bmp|Todos os arquivos|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) ApplyAvatarFile(dialog.FileName, interactive: true);
    }

    /// <summary>Usa o arquivo como avatar. O nome do arquivo só aparece na mensagem para o usuário: nunca no log (spec 005, AV7).</summary>
    void ApplyAvatarFile(string path, bool interactive)
    {
        var result = _myAvatar!.Choose(path);
        if (result.Error != null)
        {
            Log.Info("O avatar escolhido foi recusado.");
            if (interactive) MessageBox.Show(this, result.Error + "\n\nEscolha outra imagem.", "Meu avatar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Log.Info($"Avatar escolhido ({_myAvatar.PayloadFormat}, {AvatarFormatDetector.FormatSize(_myAvatar.Payload!.Length)} para os amigos).");
        if (result.Notice != null) Notify(result.Notice);
    }

    /// <summary>STREEMU_TEST_AVATAR=&lt;arquivo&gt;: aplica o avatar na abertura, sem diálogo (para testes automáticos e capturas).</summary>
    void ApplyTestAvatar()
    {
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_AVATAR") is { Length: > 0 } path) ApplyAvatarFile(path, interactive: false);
    }
}
