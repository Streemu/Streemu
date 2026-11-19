namespace Streemu.Util;

/// <summary>
/// Convite para a pesquisa com os usuários (etapa 15 do plano da telemetria). É só o gancho: o link do formulário ainda não existe e
/// fica em <see cref="Link"/>; enquanto estiver vazio nada aparece. A pesquisa é separada do diagnóstico: nada daqui sobe pela telemetria
/// nem leva dado da pessoa na URL. O app só guarda no estado local se já convidou, adiou ou a pessoa pediu para não perguntar mais.
/// </summary>
public static class ConvitePesquisa
{
    /// <summary>Endereço https do formulário. Vazio = convite desligado. Preencher antes do teste de campo; nunca com dado da pessoa na URL.</summary>
    public const string Link = "";
    /// <summary>Dias de uso antes do primeiro convite: dá tempo de a pessoa ter transmitido ou assistido algo.</summary>
    public const int DiasDeUso = 3;
    /// <summary>"Agora não" pergunta de novo só depois disto.</summary>
    public static readonly TimeSpan Adiamento = TimeSpan.FromDays(7);

    public const string Respondeu = "respondeu", NaoPerguntar = "nunca";

    public static string Titulo => "Quer ajudar a melhorar o Streemu?";
    public static string Texto => "Temos uma pesquisa curta (uns 3 minutos) para o grupo contar o que está bom e o que atrapalha. É separada do diagnóstico: o Streemu não envia nada seu por ela e as respostas são só o que você escrever lá.";

    /// <summary>O convite só abre endereço https; qualquer outra coisa (config errada) deixa o convite desligado.</summary>
    public static bool LinkValido(string link) => Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>
    /// Hora de convidar? Só com link válido, depois de <see cref="DiasDeUso"/> dias desde o primeiro uso, sem resposta nem pedido de parar,
    /// fora do adiamento e com o aviso de diagnóstico já lido (para não empilhar duas janelas).
    /// </summary>
    public static bool Deve(AppState s, DateTime agora, string link = Link)
    {
        if (!LinkValido(link)) return false;
        if (s.PesquisaEstado is Respondeu or NaoPerguntar) return false;
        if (s.TelemetriaAvisoVersao < Telemetria.TextoDiagnostico.VersaoDoAviso) return false;
        if (s.PrimeiroUsoEm == default || agora - s.PrimeiroUsoEm < TimeSpan.FromDays(DiasDeUso)) return false;
        return agora >= s.PesquisaAdiadaAte;
    }
}
