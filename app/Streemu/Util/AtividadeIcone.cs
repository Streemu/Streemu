namespace Streemu.Util;

/// <summary>O que o app está fazendo agora; cada atividade tem seu próprio ícone (janela, barra de tarefas e bandeja).</summary>
public enum AtividadeIcone { Ocioso, Assistindo, Transmitindo, Cinema }

public static class IconeAtividade
{
    /// <summary>
    /// Quando duas coisas acontecem juntas, o ícone mostra a mais "grande": Cinema (hospedando ou participando) &gt; minha live no ar &gt;
    /// assistindo a live de alguém (uma ou várias) &gt; ocioso. Função pura para o teste cobrir as 8 combinações sem abrir janela.
    /// </summary>
    public static AtividadeIcone Escolher(bool cinema, bool transmitindo, bool assistindo) =>
        cinema ? AtividadeIcone.Cinema
        : transmitindo ? AtividadeIcone.Transmitindo
        : assistindo ? AtividadeIcone.Assistindo
        : AtividadeIcone.Ocioso;

    /// <summary>Nome do recurso embutido (<c>Streemu.Arte.&lt;nome&gt;</c>) com o <c>.ico</c> da atividade.</summary>
    public static string Arquivo(AtividadeIcone atividade) => atividade switch
    {
        AtividadeIcone.Assistindo => "streemu-assistindo.ico",
        AtividadeIcone.Transmitindo => "streemu-transmitindo.ico",
        AtividadeIcone.Cinema => "streemu-cinema.ico",
        _ => "streemu.ico",
    };
}
