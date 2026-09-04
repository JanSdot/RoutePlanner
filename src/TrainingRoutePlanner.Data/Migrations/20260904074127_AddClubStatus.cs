using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrainingRoutePlanner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClubStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 1 = ClubStatus.Approved statt des sonst ueblichen 0 (Pending) -
            // Vereine, die es schon vor dieser Migration gab, wurden ohne Freigabe-Konzept
            // erstellt und sollen nicht rueckwirkend aus /clubs verschwinden. Neue Vereine
            // setzen ihren Status ohnehin immer explizit (siehe Program.cs POST /clubs), der
            // Spalten-Default wirkt effektiv nur einmalig auf die Bestandsdaten.
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Clubs",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "Clubs");
        }
    }
}
