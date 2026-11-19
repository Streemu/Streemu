using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Membros offline e apelidos locais (plano 019, etapa 7). O apelido local é só desta pessoa: <see cref="NomeNaTela(Member)"/> é o ÚNICO ponto por onde um
/// nome de amigo vira texto de tela, e o <see cref="Log.Filtro"/> garante que o apelido nunca chegue ao arquivo de log, ao relatório nem à telemetria
/// (cada frase que a tela mostra também vai ao log). Nada aqui vai para a rede: mensagens para a sala e para os amigos levam sempre o nome real.
/// </summary>
public sealed partial class MainForm
{
    ApelidosLocais? _apelidos;
    /// <summary>id → nome real (o que o amigo usa no grupo), para o filtro do log, que roda em qualquer thread e não pode tocar na sala.</summary>
    readonly Dictionary<string, string> _nomesReais = new();
    readonly object _nomesReaisGate = new();

    ApelidosLocais Apelidos => _apelidos ??= CriarApelidos();

    ApelidosLocais CriarApelidos()
    {
        var a = new ApelidosLocais(_state.ApelidosLocais);
        a.Mudou += () =>
        {
            if (!Automated) _state.Save();
            InstalarFiltroDoLog();
            RefreshSide();
            if (_room != null) RefreshMembers();
        };
        _apelidos = a;
        InstalarFiltroDoLog();
        return a;
    }

    /// <summary>O nome do amigo para mostrar na tela: o apelido local, se a pessoa deu um; senão o nome que ele usa no grupo.</summary>
    string NomeNaTela(Member m) => Apelidos.Nome(m.Id, m.Nick);
    string NomeNaTela(string id, string nickDoGrupo) => Apelidos.Nome(id, nickDoGrupo);

    /// <summary>O nome real de um amigo conhecido (nulo se nunca o vi); seguro em qualquer thread.</summary>
    string? NomeReal(string id) { lock (_nomesReaisGate) return _nomesReais.GetValueOrDefault(id); }

    /// <summary>Guarda os nomes reais de quem está na sala (online ou guardado) Chamado a cada atualização da barra.</summary>
    void GuardarNomesReais()
    {
        if (_room == null) return;
        var friends = _room.Friends.ToList();
        var ausentes = _room.Ausentes.ToList();
        lock (_nomesReaisGate)
        {
            foreach (var f in friends) _nomesReais[f.Id] = f.Nick;
            foreach (var a in ausentes) _nomesReais[a.Id] = a.Nick;
        }
    }

    /// <summary>Instala (ou tira, com zero apelidos) o filtro que troca cada apelido local pelo nome real em tudo que vai ao log.</summary>
    void InstalarFiltroDoLog() => Log.Filtro = _apelidos?.CriarFiltro(NomeReal);

    /// <summary>Os membros do grupo que estão com o app fechado, para a seção "OFFLINE" da barra: ordem alfabética pelo nome na tela, do grupo escolhido no seletor.</summary>
    IEnumerable<SideFriend> AusentesParaBarra()
    {
        if (_room == null) return [];
        var ausentes = _room.Ausentes.Where(a => a.Id != _room.MyId);
        if (_filtroGrupo != null && _salaUnida != null) ausentes = ausentes.Where(a => _salaUnida.GruposDe(a.Id).Contains(_filtroGrupo));
        return ausentes.Select(a => new SideFriend(a.Id, NomeNaTela(a.Id, a.Nick), "offline", false, false, _friendAvatars?.ImageOf(a.Id), Offline: true))
            .OrderBy(f => f.Nick, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Cabeçalho do menu da foto: com apelido, "apelido (nome real)".</summary>
    string CabecalhoDoAmigo(SideFriend amigo) =>
        Apelidos.Tem(amigo.Id) && NomeReal(amigo.Id) is { Length: > 0 } real ? $"{amigo.Nick} ({real})" : amigo.Nick;

    /// <summary>"Dar apelido..." / "Mudar apelido..." e, com apelido, "Tirar apelido" (menu da foto).</summary>
    IEnumerable<ToolStripItem> ItensDeApelido(SideFriend amigo)
    {
        bool tem = Apelidos.Tem(amigo.Id);
        yield return new ToolStripMenuItem(tem ? "Mudar apelido..." : "Dar apelido...", null, (_, _) => BeginInvoke(() => PedirApelido(amigo)));
        if (tem) yield return new ToolStripMenuItem("Tirar apelido", null, (_, _) => Apelidos.Tirar(amigo.Id));
    }

    /// <summary>A caixinha do apelido (modal, no padrão do <c>PedirNota</c>): até 24 caracteres, vazio tira. O apelido só aparece para você.</summary>
    void PedirApelido(SideFriend amigo)
    {
        string real = NomeReal(amigo.Id) ?? amigo.Nick;
        using var f = new Form
        {
            Text = "Apelido", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = $"Como você quer chamar {real}?\nSó você vê este apelido; ele não vai para o grupo.", AutoSize = true });
        var box = new TextBox { Width = UiScale.Dp(this, 300), MaxLength = ApelidosLocais.Limite, Text = Apelidos.Nome(amigo.Id, ""), PlaceholderText = real };
        var ok = new Button { Text = "Salvar", AutoSize = true, DialogResult = DialogResult.OK };
        Theme.StyleButton(ok, true);
        layout.Controls.Add(new CampoArredondado(box, 300));
        layout.Controls.Add(ok);
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var outros = OutrosNomesReais(amigo.Id);
        if (Apelidos.Problema(amigo.Id, box.Text, outros, Nick) is { } problema) { Notify(problema); return; }
        Apelidos.Definir(amigo.Id, box.Text);
    }

    /// <summary>Os nomes reais de todo mundo do grupo (online, offline e quem já vi), menos o deste amigo: um apelido não pode repetir nenhum.</summary>
    List<string> OutrosNomesReais(string id)
    {
        GuardarNomesReais();
        lock (_nomesReaisGate) return _nomesReais.Where(p => p.Key != id).Select(p => p.Value).ToList();
    }

    /// <summary>Os amigos com o nome que a tela mostra (o apelido local), para telas que só recebem a lista da sala (Transmitir, convidados da privada).</summary>
    List<Member> ComNomesDaTela(IEnumerable<Member> membros) => membros.Select(m => Apelidos.Tem(m.Id) ? m with { Nick = NomeNaTela(m) } : m).ToList();
}
