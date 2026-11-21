using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>O que o servidor do grupo diz sobre contas (<c>GET /conta/config</c>). Desde a spec 022 a conta é sempre obrigatória: não há mais "sem contas" nem "opcional".</summary>
public enum EstadoServidor
{
    /// <summary>O servidor tem contas: sem conta não se usa grupo nem transmissão.</summary>
    Obrigatoria,
    /// <summary>Não deu para perguntar (sem internet, servidor fora, 503). Nunca vira um modo sem conta: o app avisa e pergunta de novo.</summary>
    Indisponivel,
}

/// <summary>Resultado de uma operação de conta. <see cref="Texto"/> é em português, diz a próxima ação e NUNCA leva token, segredo, verificador nem código.</summary>
public sealed record ResultadoConta(bool Ok, string? Erro = null, string? Texto = null);

public sealed record ResultadoEntrada(bool Ok, string? Url, string? Texto);

/// <summary>O que a página do site faz em nome do app, com a conta já entrada (plano 019, etapa 6). O pedido sai preso à conta.</summary>
public enum AcaoConta
{
    /// <summary>Ligar o Google a uma conta de e-mail e senha.</summary>
    Vincular,
    /// <summary>Encerrar a sessão de todos os outros dispositivos. Este continua entrado.</summary>
    SairDeTodos,
    /// <summary>Apagar a conta: o servidor apaga os dados do Streemu e a página apaga o login no Firebase.</summary>
    Excluir,
}

public enum EstadoSessao
{
    /// <summary>Nunca entrou (ou saiu).</summary>
    SemConta,
    /// <summary>Sessão renovada.</summary>
    Ok,
    /// <summary>O servidor não reconhece mais esta sessão (conta ou dispositivo encerrado, segredo que não abre): as credenciais locais foram apagadas.</summary>
    Encerrada,
    /// <summary>Sem rede ou servidor fora: a credencial FICA e usa o token que tiver até vencer.</summary>
    SemRede,
}

/// <summary>
/// Conta Streemu no app (spec 019, etapa 5): entrar pelo navegador, guardar a sessão, renovar o token de acesso e sair. Não depende de tela
/// nem de <c>MainForm</c>. O segredo do dispositivo é gerado aqui e vai para o estado (cifrado pelo <see cref="Cofre"/>) ANTES de o servidor
/// conhecê-lo, e o <c>retirar</c> é idempotente por ele: se a resposta se perder, nada fica de fora. O verificador fica só em memória.
/// Nenhuma exceção sai daqui; nenhum texto de erro leva segredo.
/// </summary>
public sealed partial class Conta : IDisposable
{
    public const string FraseSessaoEncerrada = "Sua sessão foi encerrada. Entre de novo.";
    const int RenovarFaltando = 600; // renova quando faltam menos de 10 minutos

    readonly string _servidor;
    volatile string? _site;
    readonly AppState _estado;
    readonly Action _salvar;
    readonly Func<DateTimeOffset> _agora;
    readonly HttpClient _http;
    readonly SemaphoreSlim _renovando = new(1, 1);
    readonly object _gate = new();

    string? _verificador;
    string? _pedido;
    // pedido de uma ação da etapa 6 (vincular, sair de todos, excluir): separado do de entrada, que pode estar aberto ao mesmo tempo
    string? _acaoVerificador, _acaoPedido;
    AcaoConta _acaoAtual;
    string? _acesso;
    long _acessoExpira;
    string? _bilhete;
    DateTimeOffset _proximaTentativa = DateTimeOffset.MinValue;
    TimeSpan _espera = TimeSpan.FromSeconds(5);

    /// <param name="servidor">https://... do Worker do grupo.</param>
    /// <param name="salvar">Grava o estado (nos testes, não faz nada).</param>
    public Conta(string servidor, AppState estado, Action salvar, HttpMessageHandler? handler = null, Func<DateTimeOffset>? agora = null)
    {
        _servidor = servidor.TrimEnd('/');
        _estado = estado; _salvar = salvar;
        _agora = agora ?? (() => DateTimeOffset.UtcNow);
        _http = handler == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
    }

    // ------------------------------------------------------------------ o que o app sabe

    public bool TemConta => !string.IsNullOrEmpty(_estado.DispositivoId) && !string.IsNullOrEmpty(_estado.ContaSegredoProtegido);
    public string? ContaId => _estado.ContaId;
    public string? Metodo => _estado.ContaMetodo;
    /// <summary>O nome da conta, igual em todos os aparelhos (spec 027). Nulo = a conta ainda não tem nome (ou o servidor é antigo e não manda o campo).</summary>
    public string? Nome => _estado.ContaNome;
    /// <summary>O servidor já respondeu com o campo <c>nome</c> nesta execução. Sem isso, <c>Nome == null</c> não quer dizer "conta sem nome": servidor antigo nunca manda o campo.</summary>
    public bool NomeConhecido => _nomeConhecido;
    volatile bool _nomeConhecido;
    /// <summary>Quando o nome foi dado pelo servidor ou por uma troca minha. A sala pode demorar a difundir o nome novo: até lá o nome dela não vale como novidade.</summary>
    public DateTimeOffset NomeDesde { get; private set; } = DateTimeOffset.MinValue;
    /// <summary>O renovar trouxe um nome para uma conta que não tinha (o servidor adotou o apelido na migração): a tela avisa uma vez. Argumento = o nome.</summary>
    public event Action<string>? NomeAdotadoNaMigracao;
    /// <summary>Esta conta já ligou um Google (neste PC). O servidor não guarda isso: a tela só usa para esconder o botão de vincular.</summary>
    public bool GoogleVinculado => _estado.ContaGoogleVinculado;
    public IReadOnlyList<GrupoSalvo> Grupos => _estado.Grupos;
    /// <summary>Bilhete do dia (diagnóstico anônimo). Nulo antes do primeiro login ou renovação.</summary>
    public string? Bilhete { get { lock (_gate) return _bilhete; } }
    /// <summary>Avisa a tela quando a conta, o método ou os grupos mudam.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ servidor

    /// <summary>Pergunta o que o servidor faz com contas. Qualquer coisa diferente de um 200 com <c>contas: true</c> (Worker fora, 503 sem CONTA_SEGREDO) é "indisponível".</summary>
    public async Task<(EstadoServidor Estado, bool Codigo)> ConfigAsync()
    {
        var r = await ChamarAsync(HttpMethod.Get, "/conta/config", null, null);
        if (r.SemRede || r.Status != HttpStatusCode.OK || r.Json?["contas"]?.GetValue<bool>() != true) return (EstadoServidor.Indisponivel, true);
        bool codigo = r.Json["codigo"]?.GetValue<bool>() ?? true;
        // o endereço do site (www.streemu.com.br) para o link de convite; só https, só a origem. Sem ele, o link usa o próprio servidor do grupo.
        _site = Uri.TryCreate(r.Json["site"]?.GetValue<string>(), UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps
            ? site.GetLeftPart(UriPartial.Authority) : null;
        return (EstadoServidor.Obrigatoria, codigo); // o campo "obrigatoria" do servidor é ignorado: a conta é sempre obrigatória
    }

    /// <summary>Começa a entrada: gera verificador e segredo, guarda o segredo, cria o pedido e devolve o endereço da página (quem chama abre o navegador).</summary>
    public async Task<ResultadoEntrada> IniciarEntradaAsync()
    {
        var verificador = Base64Url(RandomNumberGenerator.GetBytes(32));
        var segredo = Base64Url(RandomNumberGenerator.GetBytes(32));
        var desafio = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verificador)));
        var r = await ChamarAsync(HttpMethod.Post, "/conta/login/iniciar", new JsonObject { ["desafio"] = desafio, ["acao"] = "entrar" }, null);
        if (r.SemRede) return new(false, null, TextoSemRede);
        if (r.Status != HttpStatusCode.OK || r.Json?["pedido"]?.GetValue<string>() is not string pedido)
            return new(false, null, TextoDe(r, "O servidor não aceitou começar o login. Tente de novo em instantes."));
        lock (_gate) { _verificador = verificador; _pedido = pedido; }
        // o segredo vai para o estado ANTES de o servidor conhecê-lo; o DispositivoId só vem no retirar, então GarantirAsync ainda não o usa
        _estado.ContaSegredoProtegido = Cofre.Proteger(segredo);
        if (_estado.ContaSegredoProtegido == null) return new(false, null, "Não consegui guardar a sessão neste PC (o Windows não deixou proteger o segredo). Tente de novo.");
        _salvar();
        return new(true, $"{_servidor}/entrar?pedido={pedido}", null);
    }

    /// <summary>Termina a entrada com o código de 6 dígitos que a página mostrou. Aceita espaços e hífens.</summary>
    public async Task<ResultadoConta> ConcluirEntradaAsync(string codigo, string apelido)
    {
        string? verificador, pedido;
        lock (_gate) { verificador = _verificador; pedido = _pedido; }
        var segredo = Cofre.Abrir(_estado.ContaSegredoProtegido);
        if (verificador == null || pedido == null || segredo == null)
            return new(false, "pedido_vencido", "Este pedido de login não existe mais. Clique em Entrar de novo.");
        var limpo = new string((codigo ?? "").Where(char.IsDigit).ToArray());
        if (limpo.Length != 6) return new(false, "codigo_errado", "O código tem 6 dígitos. Confira o que apareceu no navegador.");
        var corpo = new JsonObject { ["pedido"] = pedido, ["verificador"] = verificador, ["codigo"] = limpo, ["segredo"] = segredo, ["apelido"] = apelido ?? "" };
        var r = await ChamarAsync(HttpMethod.Post, "/conta/login/retirar", corpo, null);
        if (r.SemRede) return new(false, "sem_rede", TextoSemRede);
        if (r.Status != HttpStatusCode.OK || r.Json?["dispositivo"]?.GetValue<string>() is not string disp)
        {
            // pedido que acabou ou foi usado não tem mais o que tentar
            var erro = r.Json?["erro"]?.GetValue<string>();
            if (erro is "pedido_vencido" or "pedido_usado" or "muitas_tentativas") lock (_gate) { _verificador = null; _pedido = null; }
            return new(false, erro ?? "erro", TextoDe(r, "O servidor não aceitou o código. Tente de novo."));
        }
        lock (_gate) { _verificador = null; _pedido = null; }
        _estado.ContaId = r.Json["conta"]?.GetValue<string>();
        _estado.DispositivoId = disp;
        _estado.ContaMetodo = r.Json["metodo"]?.GetValue<string>();
        GuardarSessao(r.Json);
        LerNome(r.Json);
        _salvar();
        Changed?.Invoke();
        return new(true);
    }

    /// <summary>Na abertura: se há sessão, renova. Sessão que o servidor não reconhece apaga as credenciais locais.</summary>
    public async Task<EstadoSessao> GarantirAsync()
    {
        if (!TemConta) return EstadoSessao.SemConta;
        var r = await RenovarAsync();
        return r;
    }

    /// <summary>Token de acesso válido, renovando sozinho quando faltam menos de 10 minutos (ou com <paramref name="forcar"/> depois de um 401).
    /// Sem rede devolve o que tem enquanto não venceu; nunca apaga a credencial por falta de rede. Nulo = sem conta ou token vencido sem rede.</summary>
    public async Task<string?> AcessoAsync(bool forcar = false)
    {
        if (!TemConta) return null;
        long agoraS = _agora().ToUnixTimeSeconds();
        string? atual; long expira;
        lock (_gate) { atual = _acesso; expira = _acessoExpira; }
        if (!forcar && atual != null && expira - agoraS > RenovarFaltando) return atual;
        if (_agora() >= _proximaTentativa || forcar || atual == null) await RenovarAsync();
        lock (_gate) return _acesso != null && _acessoExpira > _agora().ToUnixTimeSeconds() ? _acesso : null;
    }

    async Task<EstadoSessao> RenovarAsync()
    {
        await _renovando.WaitAsync();
        try
        {
            var segredo = Cofre.Abrir(_estado.ContaSegredoProtegido);
            if (segredo == null || string.IsNullOrEmpty(_estado.DispositivoId)) { Apagar(); return EstadoSessao.Encerrada; }
            // o apelido só vai enquanto a conta não tem nome: o servidor o adota na migração (spec 027, seção 5). Com nome, ele vale mais que qualquer apelido local.
            var corpo = new JsonObject { ["dispositivo"] = _estado.DispositivoId, ["segredo"] = segredo, ["apelido"] = _estado.ContaNome == null ? ApelidoAtual?.Invoke() ?? "" : "" };
            var r = await ChamarAsync(HttpMethod.Post, "/conta/renovar", corpo, null);
            if (r.SemRede)
            {
                // espera crescente; a credencial fica
                _proximaTentativa = _agora() + _espera;
                _espera = TimeSpan.FromSeconds(Math.Min(300, _espera.TotalSeconds * 2));
                return EstadoSessao.SemRede;
            }
            if (r.Status == HttpStatusCode.OK && r.Json?["acesso"] != null)
            {
                _espera = TimeSpan.FromSeconds(5); _proximaTentativa = DateTimeOffset.MinValue;
                _estado.ContaId = r.Json["conta"]?.GetValue<string>() ?? _estado.ContaId;
                GuardarSessao(r.Json);
                var (antes, depois) = LerNome(r.Json);
                _salvar();
                Changed?.Invoke();
                if (antes == null && depois != null) NomeAdotadoNaMigracao?.Invoke(depois);
                return EstadoSessao.Ok;
            }
            if (r.Json?["erro"]?.GetValue<string>() == "conta_invalida") { Apagar(); return EstadoSessao.Encerrada; }
            // outro erro do servidor (500, 429...): não apaga nada, tenta de novo mais tarde
            _proximaTentativa = _agora() + _espera;
            _espera = TimeSpan.FromSeconds(Math.Min(300, _espera.TotalSeconds * 2));
            return EstadoSessao.SemRede;
        }
        finally { _renovando.Release(); }
    }

    /// <summary>Quem dá o apelido salvo de antes da spec 027 (o servidor o adota como nome se a conta ainda não tem). Só é lido enquanto a conta não tem nome. Opcional.</summary>
    public Func<string>? ApelidoAtual { get; set; }

    /// <summary>Sai deste PC: apaga o que é local mesmo se o servidor não responder. <c>Ok</c> falso com texto = a sessão pode continuar valendo no servidor.</summary>
    public async Task<ResultadoConta> SairAsync()
    {
        var segredo = Cofre.Abrir(_estado.ContaSegredoProtegido);
        var disp = _estado.DispositivoId;
        ChamadaResultado? r = null;
        if (segredo != null && !string.IsNullOrEmpty(disp))
            r = await ChamarAsync(HttpMethod.Post, "/conta/sair", new JsonObject { ["dispositivo"] = disp, ["segredo"] = segredo }, null);
        Apagar();
        if (r == null || r.Status == HttpStatusCode.OK) return new(true);
        return new(false, "servidor_fora", "Saí deste PC, mas o servidor não respondeu: a sessão pode continuar valendo lá até vencer. Para encerrar de vez, entre de novo e saia com internet.");
    }

    // ------------------------------------------------------------------ nome da conta (spec 027)

    /// <summary>Troca o nome da conta (<c>POST /conta/nome</c>): vale em todos os aparelhos e grupos. Só muda o que o app guarda quando o servidor responde 200;
    /// sem rede, ou com erro, nada muda e <see cref="ResultadoConta.Texto"/> diz o motivo em português. 401 renova o token e tenta uma vez mais.</summary>
    public async Task<ResultadoConta> MudarNomeAsync(string nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length == 0) return new(false, "nome_invalido", "Escreva um nome para a conta.");
        var corpo = new JsonObject { ["nome"] = limpo };
        ChamadaResultado? r = null;
        bool tinhaConta = TemConta;
        for (int tentativa = 0; tentativa < 2; tentativa++)
        {
            var token = await AcessoAsync(forcar: tentativa == 1);
            if (token == null) return new(false, "sem_sessao", TemConta ? TextoSemRede : tinhaConta ? FraseSessaoEncerrada : "Entre na sua conta primeiro.");
            r = await ChamarAsync(HttpMethod.Post, "/conta/nome", corpo, token);
            if (r.SemRede) return new(false, "sem_rede", TextoSemRede);
            if (r.Status != HttpStatusCode.Unauthorized) break;
        }
        if (r!.Status == HttpStatusCode.OK && r.Json?["nome"]?.GetValue<string>() is { Length: > 0 } novo)
        {
            _estado.ContaNome = novo; _nomeConhecido = true; NomeDesde = _agora();
            _salvar();
            Changed?.Invoke();
            return new(true);
        }
        var erro = r.Json?["erro"]?.GetValue<string>();
        if (erro == "conta_invalida") { Apagar(); return new(false, erro, FraseSessaoEncerrada); }
        return new(false, erro ?? "erro", TextoDe(r, "O servidor não aceitou o nome. Tente de novo em instantes."));
    }

    /// <summary>A sala difundiu outro nome para a minha conta (troca feita em outro aparelho). Só vale se a conta já tem nome (senão o apelido da sala é o do <c>ola</c>,
    /// não o da conta) e se a troca minha não é recente (a sala pode ainda difundir o nome antigo). Devolve verdadeiro se mudou.</summary>
    public bool AdotarNomeDaSala(string nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length == 0 || _estado.ContaNome == null || limpo == _estado.ContaNome || _agora() - NomeDesde < TimeSpan.FromSeconds(15)) return false;
        _estado.ContaNome = limpo; NomeDesde = _agora();
        _salvar();
        Changed?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ ações da conta (etapa 6)

    /// <summary>Começa vincular, sair de todos ou excluir: pede o pedido ao servidor (com o token da conta, que o prende a ela) e devolve o endereço da
    /// página (quem chama abre o navegador). Depois, <see cref="AguardarAcaoAsync"/> espera a página terminar. Não há código a digitar.</summary>
    public async Task<ResultadoEntrada> IniciarAcaoAsync(AcaoConta acao)
    {
        var verificador = Base64Url(RandomNumberGenerator.GetBytes(32));
        var desafio = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verificador)));
        var corpo = new JsonObject { ["desafio"] = desafio, ["acao"] = NomeDaAcao(acao) };
        ChamadaResultado? r = null;
        bool tinhaConta = TemConta;
        for (int tentativa = 0; tentativa < 2; tentativa++)
        {
            var token = await AcessoAsync(forcar: tentativa == 1);
            if (token == null) return new(false, null, TemConta ? TextoSemRede : tinhaConta ? FraseSessaoEncerrada : "Entre na sua conta primeiro.");
            r = await ChamarAsync(HttpMethod.Post, "/conta/login/iniciar", corpo, token);
            if (r.SemRede) return new(false, null, TextoSemRede);
            if (r.Status != HttpStatusCode.Unauthorized) break; // 401: o token pode ter vencido no meio; renova e tenta uma vez mais
        }
        if (r!.Status != HttpStatusCode.OK || r.Json?["pedido"]?.GetValue<string>() is not string pedido)
        {
            if (r.Json?["erro"]?.GetValue<string>() == "conta_invalida") { Apagar(); return new(false, null, FraseSessaoEncerrada); }
            return new(false, null, TextoDe(r, "O servidor não aceitou começar. Tente de novo em instantes."));
        }
        lock (_gate) { _acaoVerificador = verificador; _acaoPedido = pedido; _acaoAtual = acao; }
        return new(true, $"{_servidor}/entrar?pedido={pedido}", null);
    }

    /// <summary>Espera a página terminar a ação iniciada por <see cref="IniciarAcaoAsync"/> (pergunta ao servidor a cada poucos segundos, provando-se
    /// pelo verificador). Ao terminar, aplica o efeito local: excluir apaga as credenciais deste PC; vincular guarda que o Google já está ligado.
    /// Sem rede, continua esperando. <c>Erro</c> = <c>pedido_vencido</c> (15 minutos) ou <c>cancelado</c>.</summary>
    public async Task<ResultadoConta> AguardarAcaoAsync(CancellationToken ct, TimeSpan? intervalo = null)
    {
        string? verificador, pedido; AcaoConta acao;
        lock (_gate) { verificador = _acaoVerificador; pedido = _acaoPedido; acao = _acaoAtual; }
        if (verificador == null || pedido == null) return new(false, "pedido_vencido", "Este pedido não existe mais. Clique no botão de novo.");
        var limite = _agora() + TimeSpan.FromMinutes(16); // o pedido vale 15
        var corpo = new JsonObject { ["pedido"] = pedido, ["verificador"] = verificador };
        while (true)
        {
            if (ct.IsCancellationRequested) return new(false, "cancelado");
            var r = await ChamarAsync(HttpMethod.Post, "/conta/login/estado", corpo, null);
            if (!r.SemRede)
            {
                if (r.Status == HttpStatusCode.OK && r.Json?["estado"]?.GetValue<string>() == "concluido")
                {
                    lock (_gate) { _acaoVerificador = null; _acaoPedido = null; }
                    if (acao == AcaoConta.Excluir) Apagar();
                    else if (acao == AcaoConta.Vincular) { _estado.ContaGoogleVinculado = true; _salvar(); Changed?.Invoke(); }
                    return new(true);
                }
                if (r.Json?["erro"]?.GetValue<string>() == "pedido_vencido") break;
            }
            if (_agora() > limite) break;
            try { await Task.Delay(intervalo ?? TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return new(false, "cancelado"); }
        }
        lock (_gate) { _acaoVerificador = null; _acaoPedido = null; }
        return new(false, "pedido_vencido", "A página não terminou a tempo. Clique no botão de novo e conclua no navegador.");
    }

    /// <summary>Esquece o pedido de ação em andamento (a pessoa cancelou). O servidor apaga o pedido sozinho em 15 minutos.</summary>
    public void CancelarAcao() { lock (_gate) { _acaoVerificador = null; _acaoPedido = null; } }

    static string NomeDaAcao(AcaoConta a) => a switch { AcaoConta.Vincular => "vincular", AcaoConta.SairDeTodos => "sessoes", _ => "excluir" };

    // ------------------------------------------------------------------ interno

    void GuardarSessao(JsonObject j)
    {
        lock (_gate)
        {
            _acesso = j["acesso"]?.GetValue<string>();
            _acessoExpira = j["expira"]?.GetValue<long>() ?? 0;
            _bilhete = j["bilhete"]?.GetValue<string>() ?? _bilhete;
        }
        if (j["grupos"] is JsonArray gs)
            _estado.Grupos = gs.OfType<JsonObject>().Where(g => g["id"] != null)
                .Select(g => new GrupoSalvo { Id = g["id"]!.GetValue<string>(), Nome = g["nome"]?.GetValue<string>() ?? "" }).ToList();
    }

    /// <summary>Lê o <c>nome</c> do retirar/renovar. Servidor antigo não manda o campo: nada muda. Devolve (antes, depois).</summary>
    (string? Antes, string? Depois) LerNome(JsonObject j)
    {
        string? antes = _estado.ContaNome;
        if (!j.ContainsKey("nome")) return (antes, antes);
        string? n = j["nome"] is JsonValue v && v.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
        _nomeConhecido = true;
        _estado.ContaNome = n;
        if (n != antes) NomeDesde = _agora();
        return (antes, n);
    }

    void Apagar()
    {
        lock (_gate) { _acesso = null; _acessoExpira = 0; _bilhete = null; _verificador = null; _pedido = null; }
        _estado.ContaId = null; _estado.DispositivoId = null; _estado.ContaSegredoProtegido = null; _estado.ContaMetodo = null; _estado.ContaGoogleVinculado = false; _estado.Grupos = new();
        _estado.ContaNome = null; _estado.EscolhaDeNomeMostrada = false; _nomeConhecido = false;
        _salvar();
        Changed?.Invoke();
    }

    const string TextoSemRede = "Não consegui falar com o servidor do grupo. Confira a internet e tente de novo.";

    static string TextoDe(ChamadaResultado r, string padrao)
    {
        var t = r.Json?["texto"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(t) ? padrao : t; // o texto do servidor é em português e nunca leva segredo
    }

    sealed record ChamadaResultado(HttpStatusCode Status, JsonObject? Json, bool SemRede);

    async Task<ChamadaResultado> ChamarAsync(HttpMethod metodo, string caminho, JsonObject? corpo, string? bearer)
    {
        try
        {
            using var req = new HttpRequestMessage(metodo, _servidor + caminho);
            if (corpo != null) req.Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json");
            if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await _http.SendAsync(req);
            var texto = await resp.Content.ReadAsStringAsync();
            JsonObject? json = null;
            try { json = JsonNode.Parse(texto) as JsonObject; } catch { /* não é JSON */ }
            return new(resp.StatusCode, json, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            // o texto da exceção pode citar o endereço; no log vai só o tipo
            Log.Info($"Conta: sem resposta do servidor ({e.GetType().Name}).");
            return new(0, null, true);
        }
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Abre o endereço no navegador padrão. Falha não lança: quem chama mostra o endereço para copiar.</summary>
    public static bool AbrirNavegador(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}
