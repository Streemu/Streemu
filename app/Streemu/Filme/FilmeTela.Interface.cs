using System.Text.Json.Nodes;

namespace Streemu.Filme;

/// <summary>
/// O que o player desenhado no mpv (streemu-osc.lua) mostra durante o filme com o arquivo: título, papel, a linha de estado e quem está
/// na sessão, como nos mockups "Cinema Participante" e "Cinema Anfitrião" (2026-11-15). Separado do FilmeTela.cs, que já é grande.
/// </summary>
public sealed partial class FilmeTela
{
    void MandarInterface(Sessao s, Mpv mpv)
    {
        var (estado, tom) = _aviso != null && DateTime.Now < _avisoAte ? (_aviso, "aviso") : EstadoDaSessao(s);
        // com fila, a linha de baixo do título diz em que episódio está ("Episódio 3 de 12 · Você é o anfitrião")
        string papel = s.Anfitriao ? "Você é o anfitrião" : $"Com {s.NomeAnfitriao}";
        string linha = LinhaDaFila(s) is { } ep ? $"{ep} · {papel}" : papel;
        var tela = new MpvUi.EstadoTela(s.Posicao, null, s.Tocando, s.PodeControlar, s.Nome, linha, estado, tom, EmTelaCheia, PessoasDaSessao(s));
        var anfitriao = new JsonObject
        {
            ["anfitriao"] = s.Anfitriao, ["aguardando"] = s.Aguardando, ["todos"] = _todos.Checked, ["transmitir"] = _transmitir.Checked,
            ["esperar"] = _esperar.Checked,
            // a mesma regra do _esperar.Visible (o Visible de uma caixa dentro da barra escondida é sempre falso)
            ["esperarVisivel"] = s is { Anfitriao: true, Tocando: false } && s.UltimaEsperaPuloMs < 0 && s.ComecouCom == null,
            ["fila"] = FilaParaInterface(s),
        };
        MpvUi.Mandar(mpv, tela, anfitriao);
        MpvUi.LigarMouse(_tela.Handle);
    }

    /// <summary>A terceira linha: quem o anfitrião ainda espera, ou quem controla o filme.</summary>
    (string Texto, string Tom) EstadoDaSessao(Sessao s)
    {
        if (!s.Anfitriao)
        {
            if (_lig.OffsetAnfitriaoUs() == null) return ("Acertando o relógio com o anfitrião...", "espera");
            return (s.PodeControlar ? "Todos podem controlar" : $"Só {s.NomeAnfitriao} controla", "");
        }
        var faltam = s.Amigos.Where(kv => kv.Value != "pronto").Select(kv => s.NomeDe(kv.Key)).ToList();
        if (!s.Comecou && faltam.Count > 0) return ($"Esperando {MpvUi.Juntar(faltam)}", "espera");
        if (!AlguemEntrou(s)) return ("Ninguém entrou ainda", "");
        return (s.TodosControlam ? "Todos podem controlar" : "Só você controla", "");
    }

    /// <summary>
    /// Alguém além de mim está na sessão? Quem assiste pela transmissão não tem o arquivo e não entra em <c>Amigos</c>: só em <c>FluxoIds</c>.
    /// Sem contá-lo, o player dizia "Ninguém entrou ainda" com o amigo já assistindo na lista (visto em teste, 2026-11-24).
    /// </summary>
    bool AlguemEntrou(Sessao s) => s.Amigos.Count > 0 || (s.FluxoIds?.Invoke() ?? []).Any(id => id != MeuId);

    List<MpvUi.Pessoa> PessoasDaSessao(Sessao s)
    {
        if (s.Anfitriao)
        {
            var lista = new List<MpvUi.Pessoa> { new(_lig.MeuNome, "Anfitrião", "Pronto", "ok", Junto: true, Id: MeuId) };
            foreach (var (id, valor) in s.Amigos)
            {
                var (texto, tom) = MpvUi.EstadoAmigo(valor);
                lista.Add(new(s.NomeDe(id), null, texto, tom, Id: id));
            }
            // quem assiste pela transmissão não tem o arquivo e não entra nos Amigos; caso de 2026-11-18 (JoJo de um amigo): dois amigos assistiam assim e a lista só mostrava o anfitrião
            foreach (var id in s.FluxoIds?.Invoke() ?? [])
                if (!s.Amigos.ContainsKey(id) && id != MeuId) lista.Add(new(s.NomeDe(id), "Sem baixar", "Assistindo", "ok", Id: id));
            return lista;
        }
        var (eu, meuTom) = _conferindo != null ? ("Conferindo", "espera") : s.Tocando ? ("Assistindo", "ok") : ("Pronto", "ok");
        // a lista inteira, como o anfitrião a vê (filme_pessoas); eu me marco como "Você" com o estado daqui. Sem ela (anfitrião em versão antiga), só ele e eu
        if (_pessoasDoAnfitriao is { Count: > 0 } doAnfitriao)
        {
            var lista = doAnfitriao.Select(p => p.Sou(MeuId, _lig.MeuNome) ? new MpvUi.Pessoa(_lig.MeuNome, "Você", eu, meuTom, Id: MeuId) : p).ToList();
            if (!lista.Any(p => p.Sou(MeuId, _lig.MeuNome))) lista.Add(new(_lig.MeuNome, "Você", eu, meuTom, Id: MeuId));
            return lista;
        }
        return [new(s.NomeAnfitriao, "Anfitrião", "Pronto", "ok"), new(_lig.MeuNome, "Você", eu, meuTom)];
    }
}
