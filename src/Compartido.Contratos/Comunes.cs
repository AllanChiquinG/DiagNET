namespace Compartido.Contratos;

/// <summary>Aviso comprensible para el usuario. <paramref name="Codigo"/> es estable; el texto puede mejorar.</summary>
public sealed record AdvertenciaDto(string Codigo, string Mensaje, string? Dependencia = null);

/// <summary>Dato concreto que respalda un hallazgo o recomendación (rastreabilidad).</summary>
public sealed record EvidenciaDto(string Clave, string Valor);

public sealed record PaginaDto<T>(IReadOnlyList<T> Elementos, int Pagina, int TamanoPagina, int Total);

/// <summary>
/// Valor de un indicador con su disponibilidad. <see cref="Valor"/> solo tiene contenido si la disponibilidad es
/// <see cref="Disponibilidad.Conocido"/>; un <c>false</c> o un <c>0</c> conocidos nunca se confunden con «no determinado».
/// <typeparamref name="T"/> debe ser un tipo que admita nulo (<c>bool?</c>, <c>long?</c>, <c>string?</c>): así «sin valor» es <c>null</c>
/// y nunca <c>false</c> ni <c>0</c>.
/// </summary>
public sealed record IndicadorDto<T>(Disponibilidad Disponibilidad, T? Valor = default, string? Nota = null)
{
    public static IndicadorDto<T> Conocido(T valor, string? nota = null) =>
        new(Disponibilidad.Conocido, valor ?? throw new ArgumentNullException(nameof(valor)), nota);

    public static IndicadorDto<T> NoDeterminado(string? nota = null) => new(Disponibilidad.NoDeterminado, default, nota);

    public static IndicadorDto<T> NoEvaluado(string? nota = null) => new(Disponibilidad.NoEvaluado, default, nota);
}
