using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelloGameRoom.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSessionWarningSent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarningSent",
                table: "Sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WarningSent",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
