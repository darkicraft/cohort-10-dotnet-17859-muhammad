using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefreshTokenFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Legacy replacement chains cannot be re-familied safely, so force one re-login.
            migrationBuilder.Sql(
                "UPDATE [RefreshTokens] SET [RevokedAtUtc] = SYSDATETIMEOFFSET() WHERE [RevokedAtUtc] IS NULL;");
            migrationBuilder.Sql("UPDATE [RefreshTokens] SET [FamilyId] = NEWID();");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefreshTokens_FamilyId_NotEmpty",
                table: "RefreshTokens",
                sql: "[FamilyId] <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "RefreshTokens",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefreshTokens_FamilyId_NotEmpty",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "RefreshTokens");
        }
    }
}
