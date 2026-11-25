using System.Text.Json;

namespace Streemu.Util;

/// <summary>Um grupo da conta, como o servidor listou.</summary>
public sealed class GrupoSalvo
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
}

/// <summary>
/// Guarda entre aberturas: o apelido, a conta, as escolhas da interface e o que ajuda a abrir mais rápido. Fica em %LOCALAPPDATA%\Streemu\estado.json.
/// O segredo, a porta local e o código do amigo (modo por código) saíram na spec 022; o estado.json antigo continua abrindo porque o
/// leitor ignora campos que a classe não tem mais.
/// </summary>
public sealed class AppState
{
    /// <summary>OBSOLETO (spec 027): o nome agora é da conta (<see cref="ContaNome"/>). Fica só para ler o estado antigo: o apelido salvo vai uma vez ao servidor, na migração, enquanto a conta não tem nome.</summary>
    public string? Nick { get; set; }
    /// <summary>OBSOLETO (spec 027): ninguém mais lê nem grava. Fica só para o estado.json antigo continuar abrindo.</summary>
    public bool? NickManual { get; set; }
    /// <summary>O nome da conta, como o servidor deu (spec 027). Nulo = a conta ainda não tem nome ou o servidor ainda não respondeu com ele.</summary>
    public string? ContaNome { get; set; }
    /// <summary>Já mostrei a caixinha "Escolha seu nome" a esta pessoa (conta sem nome depois do renovar): só aparece uma vez.</summary>
    public bool EscolhaDeNomeMostrada { get; set; }
    /// <summary>Última conta do Discord vista (nome, id e código do avatar): na abertura o nome e a foto aparecem antes de o Discord conectar.</summary>
    public string? DiscordNome { get; set; }
    public string? DiscordId { get; set; }
    public string? DiscordAvatar { get; set; }
    /// <summary>Endereço com que a operadora entrega meus pacotes a outros clientes dela (ex.: 100.90.x.x).</summary>
    public string? AltEndpoint { get; set; }
    /// <summary>Servidor do grupo (Worker da Cloudflare) que entrega credenciais do relay de reserva.</summary>
    public string? RelayUrl { get; set; }
    /// <summary>OBSOLETO (spec 022): a senha do grupo acabou. Fica só para o estado.json antigo continuar abrindo; ninguém lê nem grava.</summary>
    public string? RelaySenha { get; set; }
    /// <summary>Volume do player (0 a 100) e mudo, guardados entre transmissões.</summary>
    public int PlayerVolumePercent { get; set; } = 100;
    public bool PlayerMuted { get; set; }
    /// <summary>Barra lateral sempre aberta: não recolhe sozinha ao assistir nem em janela estreita (Ajustes > Aparência).</summary>
    public bool SidebarPinned { get; set; }
    /// <summary>Com a barra recolhida em trilho, passar o mouse abre a navegação (Ajustes > Aparência). Padrão ligado: estado.json antigo, sem o campo, carrega como ligado.</summary>
    public bool SidebarHover { get; set; } = true;
    /// <summary>Dicas de uso discretas na interface (Ajustes > Aparência). Padrão ligado: estado.json antigo, sem o campo, carrega como ligado.</summary>
    public bool DicasDeUso { get; set; } = true;
    /// <summary>A pessoa já usou o botão de recolher a barra lateral: a dica dele nunca mais aparece.</summary>
    public bool DicaBarraVista { get; set; }
    /// <summary>Hora (ms Unix) da atividade mais nova que a pessoa já viu no painel de atividade: o ponto do ícone só acende com algo depois dela.</summary>
    public long AtividadeVista { get; set; }
    /// <summary>Escala da interface (Ctrl + / Ctrl -): 1 = normal, de 0,8 a 1,75.</summary>
    public double InterfaceScale { get; set; } = 1;
    /// <summary>Tema claro escolhido no botão da barra lateral; o padrão é o escuro.</summary>
    public bool TemaClaro { get; set; }
    /// <summary>Selo "AO VIVO" sobre a tela de quem transmite: escondido pelo usuário (padrão: aparece), canto (0 = cima/esquerda, 1 = cima/direita, 2 e 3 = embaixo) e abrir a própria live ao começar.</summary>
    public bool LiveBadgeHidden { get; set; }
    public int LiveBadgeCorner { get; set; }
    /// <summary>Como o selo aparece: estilo (0 bolinha, 1 bolinha com número, 2 pílula com texto), tamanho (0 a 2), opacidade (30 a 100 %) e se pisca.</summary>
    public int LiveBadgeStyle { get; set; }
    public int LiveBadgeSize { get; set; } = 1;
    public int LiveBadgeOpacity { get; set; } = 75;
    public bool LiveBadgeBlink { get; set; }
    public bool OpenMyLiveOnStart { get; set; }
    /// <summary>Ajustes > Aparência: não mandar aos espectadores o som de "alguém entrou/saiu" (spec 014). Falso = ligado, que é o padrão.</summary>
    public bool SomNaLiveDesligado { get; set; }
    /// <summary>Ajustes > Aparência > Avançados: não avisar por cima do jogo que a live está abaixo de 60 FPS; o card na janela do Streemu continua. Falso = aviso ligado (padrão).</summary>
    public bool OfertaFpsSobreOJogoDesligada { get; set; }
    /// <summary>Usa o avatar do Discord (quando não há imagem escolhida no PC). Ligado por padrão (spec 005).</summary>
    public bool UsarAvatarDiscord { get; set; } = true;
    /// <summary>Onde a janela pequena do player ficou (largura 0 = nunca foi usada).</summary>
    public int MiniX { get; set; }
    public int MiniY { get; set; }
    public int MiniWidth { get; set; }
    public int MiniHeight { get; set; }
    /// <summary>Pipeline de encoder que funcionou da última vez, por GPU e tamanho de fonte (ver <see cref="PipelineMemory"/>).</summary>
    public Dictionary<string, string> PipelineMemory { get; set; } = new();
    /// <summary>Placa (com o driver) em que o Vulkan do ffplay falhou e quantas vezes; com duas falhas o app abre as lives direto pelo Direct3D 11 até o reteste de 30 dias (ver <see cref="Media.VulkanMemoria"/>). Nulo = sem falha lembrada.</summary>
    public Media.VulkanBloqueio? VulkanBloqueio { get; set; }
    /// <summary>Id desta instalação na sala do grupo (não expira: é como os amigos acham você).</summary>
    public string? InstallId { get; set; }
    /// <summary>Última escolha da janela Transmitir: altura (0 = Fonte), FPS, modo de áudio e o processo da janela.</summary>
    public int GoLiveHeight { get; set; } = 0; // 0 = Original (Fonte): padrão até a pessoa escolher outra qualidade (issue #173)
    /// <summary>Migração do padrão para Original já aplicada neste estado (ver <see cref="Load"/>).</summary>
    public bool GoLiveOriginalPadrao { get; set; }
    public int GoLiveFps { get; set; } = 60;
    public int GoLiveAudio { get; set; }
    public string? GoLiveProcess { get; set; }
    /// <summary>Experimental: no Discord, botão "Participar" nativo (sem navegador) no lugar do botão-link "Assistir".</summary>
    public bool DiscordJoinNativo { get; set; }
    /// <summary>Não avisar quando um amigo começa uma live ou abre um filme (Ajustes > Assistir; os avisos vêm ligados).</summary>
    public bool AvisosDesligados { get; set; }
    /// <summary>Canal da atualização automática (Configurações > Sistema): "nightly" (padrão) ou "stable". Ver <see cref="Updater.NormalizarCanal"/>.</summary>
    public string CanalAtualizacao { get; set; } = Updater.CanalNightly;
    /// <summary>Sino de silenciar (spec 012): "até ligar de novo" e, com prazo, o fim dele em milissegundos Unix (0 = sem prazo).</summary>
    public bool SilencioIndefinido { get; set; }
    /// <summary>Já viu, uma vez, o aviso de que fechar a janela só esconde o Streemu na bandeja (guardado: o sino silenciado não pode escondê-lo para sempre).</summary>
    public bool DicaBandejaVista { get; set; }
    public long SilencioAteUnixMs { get; set; }
    /// <summary>Ids (na sala) dos amigos cujos avisos de live e filme foram calados pelo menu da foto (spec 013).</summary>
    public List<string> AmigosSilenciados { get; set; } = new();
    /// <summary>Apelidos que só esta pessoa vê (plano 019, etapa 7): id da conta do amigo → apelido (até 24 caracteres). Nunca sai do PC: não vai ao log nem à telemetria.</summary>
    public Dictionary<string, string> ApelidosLocais { get; set; } = new();
    /// <summary>Telemetria (spec 009): ligada por padrão; a pessoa desliga em Ajustes > Diagnóstico.</summary>
    public bool TelemetriaDesligada { get; set; }
    /// <summary>Id aleatório do diagnóstico anônimo (quando a pessoa desliga o diagnóstico): não vem do Windows, do Discord nem do InstallId, então não liga o que sobe anônimo ao que já subiu com o nome.</summary>
    public string? TelemetriaIdAnonimo { get; set; }
    /// <summary>Já viu o aviso de que o app manda diagnóstico (mostrado uma vez, na primeira abertura com a telemetria).</summary>
    public bool TelemetriaAvisoVisto { get; set; }
    /// <summary>Qual versão do aviso de diagnóstico a pessoa já leu (<see cref="Telemetria.TextoDiagnostico.VersaoDoAviso"/>): o aviso volta uma vez quando o texto muda de um jeito que importa.</summary>
    public int TelemetriaAvisoVersao { get; set; }
    /// <summary>Hash do perfil do PC que o servidor já tem: o perfil completo só vai de novo quando muda (ou a cada 7 dias).</summary>
    /// <summary>Dia do primeiro uso (para o convite da pesquisa esperar alguns dias). Gravado uma vez.</summary>
    public DateTime PrimeiroUsoEm { get; set; }
    /// <summary>"respondeu", "nunca" (não perguntar mais) ou vazio. Só local; não vai para a telemetria.</summary>
    public string? PesquisaEstado { get; set; }
    /// <summary>"Agora não" no convite da pesquisa: só pergunta de novo depois desta data.</summary>
    public DateTime PesquisaAdiadaAte { get; set; }
    /// <summary>Conta Streemu (spec 019). Só existem depois de entrar. A sessão dura até a pessoa sair.</summary>
    public string? ContaId { get; set; }
    public string? DispositivoId { get; set; }
    /// <summary>Segredo do dispositivo, cifrado pelo <see cref="Cofre"/> (base64). Nunca em texto puro, nunca em log.</summary>
    public string? ContaSegredoProtegido { get; set; }
    /// <summary>Como entrou: "google.com" ou "password".</summary>
    public string? ContaMetodo { get; set; }
    /// <summary>Esta conta ligou um Google depois de criada por e-mail e senha (etapa 6). Só some o botão "Vincular o Google"; o servidor não guarda isso.</summary>
    public bool ContaGoogleVinculado { get; set; }
    /// <summary>Grupos da conta como o servidor disse da última vez (a sala é a fonte da verdade; isto só evita uma tela vazia ao abrir).</summary>
    public List<GrupoSalvo> Grupos { get; set; } = new();
    /// <summary>Chaves (<see cref="Novidades.Chave"/>) das novidades que a pessoa já viu, até 400. Nulo = nunca abriu um app com a tela Novidades: na primeira vez marca tudo como visto, sem selo.</summary>
    public List<string>? NovidadesVistas { get; set; }
    public string? TelemetriaPerfilHash { get; set; }
    public DateTime TelemetriaPerfilEm { get; set; }
    public DateTime SavedAt { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "estado.json");

    /// <summary>STREEMU_NOSTATE=1: não lê nem grava o estado (testes que não podem mexer no código salvo do usuário).</summary>
    static readonly bool Disabled = Environment.GetEnvironmentVariable("STREEMU_NOSTATE") == "1";
    /// <summary>Falso com STREEMU_NOSTATE=1: nada que o usuário escolheu deve ser lido nem gravado no disco (também vale para o avatar).</summary>
    public static bool Persists => !Disabled;

    public static AppState Load()
    {
        if (Disabled) return new AppState();
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    // migração única (issue #173): todo mundo cai em Original, inclusive quem já tinha 1080 salvo; depois disso a escolha da pessoa vale
                    if (!s.GoLiveOriginalPadrao) { s.GoLiveHeight = 0; s.GoLiveOriginalPadrao = true; }
                    return s;
                }
            }
        }
        catch (Exception e) { Log.Info("Não consegui ler o estado salvo: " + e.Message); }
        return new AppState();
    }

    public void Save()
    {
        if (Disabled) return;
        try
        {
            SavedAt = DateTime.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Grava num temporário e troca de uma vez: quem lê no meio (outra instância, o exe novo da atualização automática)
            // pegava o arquivo pela metade, o Load falhava e o app voltava "zerado", com apelido sumido e InstallId NOVO
            // (uma pessoa virava várias na sala do grupo; ver DESCOBERTAS 2026-11-08).
            string temp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) { Log.Info("Não consegui salvar o estado: " + e.Message); }
    }
}
