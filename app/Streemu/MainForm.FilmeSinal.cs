using Streemu.Filme;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// O sinal do anfitrião do filme (spec 010, 7.5). Caso de campo de 2026-11-28: o PC do anfitrião travou e o espectador ficou "conectado" para sempre, porque ninguém olhava o
/// túnel da ligação do filme (ele vira Lost depois de 8 s sem pacotes). Só as ligações do filme (<c>_filmeLinks</c>); o Tunnel e a live não mudam.
/// Quem assiste: Lost só mostra um aviso e o filme continua (oscilação curta de Wi-Fi não interrompe nada); voltou antes de 30 s, o aviso some e o estado periódico do
/// anfitrião ressincroniza sozinho; 30 s seguidos sem sinal, pausa e avisa que ele caiu. Quem serve: o espectador sem sinal só aparece na lista como "sem sinal".
/// A regra do tempo mora em <see cref="SinalDoAnfitriao"/> (Filme/SinalDoFilme.cs, com teste).
/// </summary>
public partial class MainForm
{
    const string TextoSemSinal = "Sem sinal do anfitrião... o filme continua e se acerta sozinho quando a conexão voltar.";
    const string TextoAnfitriaoCaiu = "O anfitrião caiu (o PC dele pode ter travado). Quando ele voltar, entre de novo.";

    /// <summary>Um vigia por ligação do filme, pela sessão. Só a thread da tela mexe.</summary>
    readonly Dictionary<string, VigiaSinalFilme> _filmeSinal = new();

    void VigiarSinalFilme(PeerLink link)
    {
        SoltarSinalFilme(link.SessionId);
        _filmeSinal[link.SessionId] = new VigiaSinalFilme(this, link);
    }

    /// <summary>Tira a assinatura do túnel e para o relógio (a ligação acabou): nada fica preso ao túnel nem ao MainForm.</summary>
    void SoltarSinalFilme(string sid)
    {
        if (_filmeSinal.Remove(sid, out var v)) v.Dispose();
    }

    /// <summary>Quem assiste: o túnel com o anfitrião perdeu o sinal. O filme continua; só o aviso fica na tela.</summary>
    void FilmeSemSinal(PeerLink link)
    {
        Log.Info("[filme] sem sinal do anfitrião (8 s sem pacotes): o filme continua tocando; se passar de 30 s, pauso e encerro a ligação.");
        AvisoFixoDoFilme(link, TextoSemSinal);
    }

    /// <summary>Quem assiste: o sinal voltou antes do limite. O aviso some e o próximo estado do anfitrião (a cada 3 s) corrige o ponto, se precisar.</summary>
    void FilmeSinalVoltou(PeerLink link, TimeSpan duracao)
    {
        Log.Info($"[filme] o sinal do anfitrião voltou depois de {duracao.TotalSeconds:0} s; o próximo estado dele ressincroniza.");
        AvisoFixoDoFilme(link, null);
    }

    /// <summary>
    /// Quem assiste: 30 s seguidos sem sinal. Pausa o filme aqui, deixa o aviso na tela e encerra a ligação (avisando o anfitrião pela sala, se ele estiver vivo).
    /// A janela fica aberta para a pessoa ler e sair: nada fecha de surpresa.
    /// </summary>
    void FilmeAnfitriaoCaiu(PeerLink link)
    {
        string sid = link.SessionId;
        if (sid == _streamSid && _streamTela is { IsDisposed: false } st)
        {
            Log.Info("[filme] o anfitrião ficou 30 s sem sinal: o vídeo pela transmissão já parou; encerrei a ligação e deixei a janela aberta com o aviso.");
            st.AnfitriaoCaiu(TextoAnfitriaoCaiu);
            _streamSid = null; // o FimStreamDaLigacao fecharia a janela do player se ainda a achasse ligada a esta ligação; ela fica para a pessoa ler o aviso
        }
        else if (_filmeForm is { IsDisposed: false, Sessao: { Anfitriao: false } } tela && sid == _filmeSessaoSid)
        {
            Log.Info("[filme] o anfitrião ficou 30 s sem sinal: pausei o filme e encerrei a ligação; a janela fica aberta com o aviso.");
            tela.AnfitriaoCaiu(TextoAnfitriaoCaiu);
            _filmeSessaoSid = null; // o "Sair" da janela não tem mais ligação para desfazer
        }
        else
        {
            // ligação que não é do player (baixando o arquivo, por exemplo): o download tem o próprio tratamento de queda; aqui só o registro
            Log.Info("[filme] o anfitrião ficou 30 s sem sinal numa ligação que não é a do player; não mexi nela.");
            return;
        }
        Notify(TextoAnfitriaoCaiu);
        SairLigacao(sid); // avisa o anfitrião pela sala (se ele voltar, solta esta ligação) e encerra: FimLigacaoFilme tira o vigia
    }

    /// <summary>O aviso fixo da janela a que esta ligação pertence (a do player pela transmissão ou a do filme com o arquivo); outra ligação não tem janela.</summary>
    void AvisoFixoDoFilme(PeerLink link, string? texto)
    {
        if (link.SessionId == _streamSid) _streamTela?.AvisoFixo(texto);
        else if (link.SessionId == _filmeSessaoSid && _filmeForm?.Sessao is { Anfitriao: false }) _filmeForm.AvisoFixo(texto);
    }

    /// <summary>
    /// Quem serve: a pessoa fica "sem sinal" na lista quando TODAS as ligações dela com este filme estão perdidas (ela pode ter duas: o player e o download).
    /// Nada é pausado nem recusado por isso; o anfitrião e os outros seguem normalmente.
    /// </summary>
    void AtualizarSemSinalDoAmigo(string peerId)
    {
        var dele = _filmeLinks.Values.Where(l => l.IAmSender && l.PeerId == peerId).ToList();
        bool sem = dele.Count > 0 && dele.All(l => l.Tunnel.State == TunnelState.Lost);
        if (_filmeForm?.Sessao is { Anfitriao: true } s && s.SemSinalDe(peerId) != sem)
        {
            Log.Info(sem ? "[filme] um espectador ficou sem sinal; aparece como \"sem sinal\" na lista e o filme segue." : "[filme] um espectador que estava sem sinal voltou.");
            s.MarcarSemSinal(peerId, sem);
            _filmeForm.PessoasMudaram(); // a lista "Na sessão" de todos
        }
    }

    /// <summary>
    /// Olha o túnel de UMA ligação do filme. O evento do túnel vem de outra thread: tudo passa pelo <c>Ui</c>. O relógio de 1 s só existe enquanto há um período sem sinal
    /// (nenhum custo no resto do filme).
    /// </summary>
    sealed class VigiaSinalFilme : IDisposable
    {
        readonly MainForm _f;
        readonly PeerLink _link;
        readonly SinalDoAnfitriao _sinal = new();
        readonly Action<TunnelState, string> _aoMudar;
        System.Windows.Forms.Timer? _relogio;
        bool _fim;

        public VigiaSinalFilme(MainForm f, PeerLink link)
        {
            _f = f; _link = link;
            _aoMudar = (estado, _) => _f.Ui(() => Estado(estado));
            link.Tunnel.StateChanged += _aoMudar;
        }

        void Estado(TunnelState estado)
        {
            if (_fim) return;
            if (_link.IAmSender) { _f.AtualizarSemSinalDoAmigo(_link.PeerId); return; }
            bool mudou = estado == TunnelState.Connected ? _sinal.Conectou()
                : estado is TunnelState.Lost or TunnelState.Failed ? _sinal.Perdeu()
                : false;
            if (mudou)
            {
                if (_sinal.Fase == FaseDoSinal.SemSinal) { LigarRelogio(); _f.FilmeSemSinal(_link); }
                else { PararRelogio(); _f.FilmeSinalVoltou(_link, _sinal.UltimaQueda); }
            }
        }

        void LigarRelogio()
        {
            if (_relogio != null) return;
            _relogio = new System.Windows.Forms.Timer { Interval = 1000 };
            _relogio.Tick += (_, _) =>
            {
                if (_fim || !_sinal.Passou()) return;
                PararRelogio();
                _f.FilmeAnfitriaoCaiu(_link); // acaba em FimLigacaoFilme, que chama o Dispose daqui
            };
            _relogio.Start();
        }

        void PararRelogio() { _relogio?.Stop(); _relogio?.Dispose(); _relogio = null; }

        public void Dispose()
        {
            _fim = true;
            _link.Tunnel.StateChanged -= _aoMudar;
            PararRelogio();
        }
    }
}
