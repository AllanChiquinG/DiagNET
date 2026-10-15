using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servidor.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class AmpliarMetadatoLibreria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "licencia",
                table: "metadato_libreria",
                type: "text",
                nullable: true,
                comment: "Expresión de licencia o URL, tal como la publica NuGet (sin límite de longitud: una URL puede ser larga).",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paquete_alterno",
                table: "metadato_libreria",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                comment: "Reemplazo declarado por el autor al deprecar el paquete (NULL si no hay o no se determinó).");

            migrationBuilder.AddColumn<string>(
                name: "rango_alterno",
                table: "metadato_libreria",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                comment: "Rango de versiones del paquete alterno, tal como lo declara NuGet.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Up (varchar(200) → text, más dos columnas nuevas anulables) conserva todos los datos. Revertir NO puede conservar una licencia de más de
            // 200 caracteres: solo en ese caso se pone a NULL (es una caché regenerable desde NuGet; los diagnósticos guardados no se tocan).
            migrationBuilder.Sql("UPDATE metadato_libreria SET licencia = NULL WHERE char_length(licencia) > 200;");

            migrationBuilder.DropColumn(
                name: "paquete_alterno",
                table: "metadato_libreria");

            migrationBuilder.DropColumn(
                name: "rango_alterno",
                table: "metadato_libreria");

            migrationBuilder.AlterColumn<string>(
                name: "licencia",
                table: "metadato_libreria",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldComment: "Expresión de licencia o URL, tal como la publica NuGet (sin límite de longitud: una URL puede ser larga).");
        }
    }
}
