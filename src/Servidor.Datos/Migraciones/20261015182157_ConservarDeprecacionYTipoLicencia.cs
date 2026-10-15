using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servidor.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class ConservarDeprecacionYTipoLicencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "deprecacion_mensaje",
                table: "metadato_libreria",
                type: "text",
                nullable: true,
                comment: "Mensaje de la deprecación (texto externo tratado como dato; acotado a 1000 caracteres al leerlo).");

            migrationBuilder.AddColumn<string>(
                name: "deprecacion_razones",
                table: "metadato_libreria",
                type: "jsonb",
                nullable: true,
                comment: "Razones de la deprecación publicadas por NuGet (lista JSON); NULL si no está deprecado o no se determinó.");

            migrationBuilder.AddColumn<string>(
                name: "tipo_licencia",
                table: "metadato_libreria",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                comment: "Tipo de licencia RECIBIDO de NuGet (expresion, url o archivo). NULL = desconocido: nunca se deduce del texto.");

            migrationBuilder.AddCheckConstraint(
                name: "ck_metadato_tipo_licencia",
                table: "metadato_libreria",
                sql: "tipo_licencia IS NULL OR tipo_licencia IN ('expresion','url','archivo')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_metadato_tipo_licencia",
                table: "metadato_libreria");

            migrationBuilder.DropColumn(
                name: "deprecacion_mensaje",
                table: "metadato_libreria");

            migrationBuilder.DropColumn(
                name: "deprecacion_razones",
                table: "metadato_libreria");

            migrationBuilder.DropColumn(
                name: "tipo_licencia",
                table: "metadato_libreria");
        }
    }
}
