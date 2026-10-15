namespace Compartido.Contratos;

/// <summary>Límites de la entrada compartidos por cliente (validación previa) y servidor (validación definitiva).</summary>
public static class LimitesEntrada
{
    /// <summary>Tamaño máximo del archivo .csproj: 1 MB.</summary>
    public const long TamanoMaximoArchivoBytes = 1024 * 1024;

    /// <summary>Tamaño máximo del cuerpo HTTP: 2 MB (deja margen para los encabezados del formulario).</summary>
    public const long TamanoMaximoCuerpoBytes = 2 * 1024 * 1024;

    /// <summary>Máximo de dependencias directas distintas por análisis (DEC-09, confirmada por el propietario).</summary>
    public const int MaximoDependencias = 100;
}
