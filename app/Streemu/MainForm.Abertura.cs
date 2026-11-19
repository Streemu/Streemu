using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Streemu.Media;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A abertura do app (evento Load), sem o túnel do modo por código (spec 022): conferência das ferramentas, log do PC, endereço
/// alternativo (CGNAT/UPnP) que a sala põe no código de cada ligação, firewall e o despacho dos testes automáticos.
/// Cada ligação da sala descobre sozinha o endereço público dela (PeerLink.CreateAsync); aqui só mora o que é do app inteiro.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// IP do roteador dentro da operadora (100.64/10 ou privado), usado em <see cref="CarrierIp"/>. Só o IP importa: a porta é a de cada ligação.
    /// </summary>
    IPAddress? _alt;

    async Task IniciarAsync()
    {
        IniciarBinarios();
        Log.Info($"Windows: {SystemInfo.Windows}; GPU: {string.Join(" | ", SystemInfo.Gpus())}");
        if (!SystemInfo.ProcessAudioSupported) Log.Info("Aviso: este Windows não suporta áudio por processo (precisa do 10 2004+).");
        try { _alt = await DiscoverAltAsync(); }
        catch (Exception e) { Log.Info("Não consegui descobrir o endereço do roteador dentro da operadora: " + e.Message + ". Só afeta amigos da mesma operadora; feche e abra o app para tentar de novo."); }

        if (TestDir != null) { DespacharTeste(); return; }
        if (_localMode) return;
        _state.Save(); // grava o AltEndpoint, se descobriu um
        CheckFirewallAtStartup(); // MainForm.Firewall.cs
    }

    /// <summary>
    /// ffmpeg, ffplay e mpv: só rodam se forem os que vêm com o Streemu e baterem com o SHA-256 do pin (Util/Binarios.cs). A conferência começa já na abertura,
    /// em segundo plano, para a primeira live não esperar ~1 s de SHA-256. Arquivo diferente ou faltando: o aviso aparece na linha de status (e na bandeja se o
    /// app não estiver em foco); quando o reparo sozinho não vai dar certo (sem conta, download falhou) vira uma janela. Em teste automático só vai para o log:
    /// a janela de erro aparecia na frente de quem usava o PC e travava o teste (2026-11-16).
    /// </summary>
    void IniciarBinarios()
    {
        Binarios.Servidor = () => RelayConfig.Load(_state);
        Binarios.Aviso = (texto, precisaDeVoce) => Ui(() =>
        {
            Log.Info("[binarios] " + texto);
            if (Automated) return;
            _groupStatus.Text = texto;
            if (Form.ActiveForm == null) MostrarBalao(8000, UiBrand.Name, texto, ToolTipIcon.Warning);
            if (precisaDeVoce) MessageBox.Show(this, texto, UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        Binarios.IniciarConferencia();
    }

    /// <summary>
    /// Os testes automáticos começam aqui. O par por código (STREEMU_TEST_DIR sem STREEMU_TEST_ROOM) deixou de existir na spec 022: falha logo e claro,
    /// em vez de ficar parado até o tempo limite da bateria.
    /// </summary>
    void DespacharTeste()
    {
        if (TesteRetomarTelemetria) _ = RunTestRetomadaAsync();
        else if (TestRoom) _ = RunRoomTestAsync();
        else
        {
            Log.Info("teste por código não existe mais: use STREEMU_TEST_ROOM=1 (a conexão só acontece pela sala do grupo).");
            Environment.ExitCode = 2;
            ExitApp();
        }
    }

    /// <summary>
    /// Endereço alternativo para o código das ligações: o IP WAN do roteador quando ele está na faixa do CGNAT (100.64/10).
    /// A operadora de vocês entrega pacotes entre clientes com esse endereço, e o NAT do amigo precisa
    /// "conhecer" ele para deixar entrar. Vem do UPnP do roteador ou do que um amigo já viu (seen_as, <see cref="AprenderSeenAs"/>).
    /// </summary>
    async Task<IPAddress?> DiscoverAltAsync()
    {
        if (TestDir != null)
            return Environment.GetEnvironmentVariable("STREEMU_TEST_ALT") == "1" ? IPAddress.Parse("127.0.0.2") : null;
        if (_localMode) return null;
        // Com endereço já guardado: usa na hora e confere em segundo plano (o UPnP do Windows leva ~9 s).
        if (_state.AltEndpoint != null && IPEndPoint.TryParse(_state.AltEndpoint, out var cached) && Upnp.IsCarrier(cached.Address))
        {
            Log.Info($"Endereço dentro da operadora (guardado): {Tunnel.Mask(new IPEndPoint(cached.Address, 0))}. Conferindo com o roteador em segundo plano.");
            _ = Task.Run(async () =>
            {
                var w = await Upnp.GetWanIpAsync(TimeSpan.FromSeconds(20));
                if (w != null && Upnp.IsCarrier(w) && !w.Equals(cached.Address))
                    Ui(() =>
                    {
                        _alt = w;
                        GuardarAlt(w);
                        Log.Info("O endereço do seu roteador dentro da operadora mudou; as próximas ligações já usam o novo.");
                    });
            });
            return cached.Address;
        }
        Log.Info("Perguntando ao roteador o endereço dentro da operadora (até 15 s)...");
        var wan = await Upnp.GetWanIpAsync(TimeSpan.FromSeconds(15));
        if (wan != null)
        {
            // um IP WAN que é do CGNAT ou privado nunca é o público: não precisa do STUN para saber que há uma operadora no meio
            if (Upnp.IsCarrier(wan))
            {
                Log.Info($"Seu roteador está atrás do CGNAT da operadora ({Tunnel.Mask(new IPEndPoint(wan, 0))}). Esse endereço vai no código das ligações, para amigos da mesma operadora.");
                GuardarAlt(wan);
                return wan;
            }
            if (Upnp.IsCarrierOrPrivate(wan))
                Log.Info($"Seu roteador tem IP WAN privado ({wan}): há outro roteador antes dele (NAT duplo). Isso pode atrapalhar a conexão direta.");
        }
        else Log.Info("O roteador não respondeu ao UPnP (ou está desligado nele). Se precisar, o endereço alternativo é aprendido com o amigo.");
        if (_state.AltEndpoint != null && IPEndPoint.TryParse(_state.AltEndpoint, out var learned))
        {
            Log.Info($"Usando o endereço dentro da operadora aprendido antes: {Tunnel.Mask(new IPEndPoint(learned.Address, 0))}.");
            return learned.Address;
        }
        return null;
    }

    void GuardarAlt(IPAddress ip) => _state.AltEndpoint = new IPEndPoint(ip, 0).ToString(); // o estado antigo tinha IP:porta; a porta nunca foi lida

    /// <summary>
    /// O amigo contou de que endereço os pacotes meus chegam nele ("seen_as", Net/Tunnel.cs). Se for um endereço da operadora que eu não conhecia
    /// (UPnP mudo, roteador em NAT duplo), guarda: as próximas ligações o levam no código. Antes só o túnel do modo por código ouvia isto.
    /// </summary>
    void AprenderSeenAs(PeerLink link, JsonObject msg)
    {
        if (msg["type"]?.GetValue<string>() != "seen_as" || Automated) return;
        if (!IPEndPoint.TryParse(msg["ep"]?.GetValue<string>() ?? "", out var seen) || seen.AddressFamily != AddressFamily.InterNetwork) return;
        if (link.Tunnel.PublicEndpoint is not { } pub || seen.Equals(pub) || seen.Address.Equals(_alt)) return;
        Log.Info($"{link.PeerNick} recebe seus pacotes vindos de {Tunnel.Mask(seen)}, e não do seu endereço público.");
        if (!Upnp.IsCarrierOrPrivate(seen.Address)) return;
        _alt = seen.Address;
        GuardarAlt(seen.Address);
        _state.Save();
        Log.Info($"{link.PeerNick} mostrou o seu endereço dentro da operadora; as próximas ligações o levam no código.");
    }
}
