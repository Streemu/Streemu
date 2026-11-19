using System.Text.Json.Nodes;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A entrada numa sessão (11.1; mockup "Cinema Formas de entrada", 2026-11-15): o que o painel mostra em cada caso. Sem anúncio, criar a
/// própria sessão; com o anúncio de um amigo, os três jeitos de entrar; baixando, o progresso e o "Parar"; sessão que sumiu, só o aviso.
/// Os botões e os cliques continuam os da FilmeTela.
/// </summary>
public sealed partial class FilmeTela
{
    void AtualizarCard(Sessao? s, JsonObject? anuncio)
    {
        if (s != null || !_selecao.Detail || _listaCinema) return;
        string? semRede = _lig.SemRede();
        if (_selecao.Missing && !Ocupado) { MostrarEncerrada(anuncio); return; }
        if (_baixando != null) { MostrarBaixando(anuncio); return; }
        if (!Ocupado) _tenho.Enabled = true;
        if (anuncio == null) MostrarCriar(semRede);
        else MostrarEntrada(anuncio, semRede);
    }

    void Cabecalho(JsonObject? anuncio, string titulo)
    {
        string dono = (string?)anuncio?["anfitriao"] ?? "um amigo";
        _card.ShowHeader(titulo, $"Com {dono}", CinemaStatus.Of((string?)anuncio?["estado"]), (string?)anuncio?["filme"] ?? titulo, poster: true, CapaDe(anuncio));
    }

    void MostrarEncerrada(JsonObject? anuncio)
    {
        Cabecalho(anuncio, (string?)anuncio?["nome"] ?? "Sessão encerrada");
        _principal.Enabled = _tenho.Enabled = _stream.Enabled = false;
        _card.ShowOptions("Esta sessão não está mais disponível", [], "Volte ao Cinema e escolha uma sessão atual.");
    }

    void MostrarBaixando(JsonObject? anuncio)
    {
        Cabecalho(anuncio, (string?)anuncio?["nome"] ?? "Filme");
        Texto(_principal, "Parar o download");
        Estilo(_principal, primario: false);
        _card.ShowOptions("Baixando o filme", [new("Baixar e assistir", _status.Text, _principal)],
            "O que já veio fica guardado: dá para fechar o app e continuar depois.");
    }

    void MostrarCriar(string? semRede)
    {
        _card.ShowHeader("Assistir um filme com o grupo", "", null, "", poster: false);
        Texto(_principal, "Escolher arquivo");
        Estilo(_principal, primario: true);
        _principal.Enabled = semRede == null && _tenho.Enabled;
        _card.ShowOptions("Escolha o filme", [new("Um filme do seu PC", "O grupo fica sabendo e quem quiser entra: com o mesmo arquivo, ou baixando de você.", _principal)],
            semRede ?? "O arquivo vai direto do seu PC para o dos amigos, sem servidor no meio.");
    }

    void MostrarEntrada(JsonObject anuncio, string? semRede)
    {
        string dono = (string?)anuncio["anfitriao"] ?? "o amigo", filme = (string?)anuncio["filme"] ?? "";
        Cabecalho(anuncio, (string?)anuncio["nome"] ?? "Filme");
        long tam = Download.TamanhoDe(filme);
        // download parado na última pasta: o botão já diz que continua (conferido a cada 2 s, é leitura de disco)
        ConsultarParcial(filme);
        bool continua = _parcial is long feito && tam > 0;
        Texto(_principal, continua ? "Continuar e assistir" : "Baixar e assistir");
        string baixar = continua ? $"Continua de onde parou ({_parcial * 100 / tam}% de {Tamanho(tam)}) e entra após a conferência."
            : $"Baixe o arquivo original ({Tamanho(tam)}) e entre após a conferência.";
        bool comStream = anuncio["stream"]?.GetValue<bool>() == true;
        Texto(_stream, "Assistir agora");
        _stream.Enabled = comStream && semRede == null && !Participando;
        _dica.SetToolTip(_stream, comStream ? "Vê agora pela conexão direta, sem baixar o arquivo. A imagem sai em qualidade menor que a do arquivo." : $"{dono} ainda não ligou \"Permitir assistir sem baixar\".");
        string semBaixar = comStream ? $"Pela transmissão de {dono}; a qualidade pode ser reduzida." : $"{dono} ainda não ligou \"Permitir assistir sem baixar\" na sessão.";
        Texto(_tenho, "Escolher arquivo");
        Estilo(_principal, primario: false);
        _principal.Enabled = semRede == null && _tenho.Enabled;
        _card.ShowOptions("Como você quer assistir?",
        [
            new("Assistir sem baixar", semBaixar, _stream),
            new("Baixar e assistir", baixar, _principal),
            new("Já tenho o arquivo", "Escolha seu arquivo para conferir se corresponde ao da sessão.", _tenho),
        ], semRede ?? "");
    }

    /// <summary>O botão principal da caixa é roxo; os outros jeitos de entrar usam o roxo apagado do mockup.</summary>
    static void Estilo(Button b, bool primario)
    {
        b.BackColor = primario ? Theme.Accent : Theme.AccentMuted;
        b.FlatAppearance.MouseOverBackColor = primario ? Theme.AccentHover : Theme.AccentMutedHover;
    }
}
