using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(EntityDataContext))]
    [Migration("20260531120000_AddAuctionListingEscrowStorage")]
    public partial class AddAuctionListingEscrowStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EscrowStorageId",
                schema: "data",
                table: "AuctionListing",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_EscrowStorageId",
                schema: "data",
                table: "AuctionListing",
                column: "EscrowStorageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionListing_ItemStorage_EscrowStorageId",
                schema: "data",
                table: "AuctionListing",
                column: "EscrowStorageId",
                principalSchema: "data",
                principalTable: "ItemStorage",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionListing_ItemStorage_EscrowStorageId",
                schema: "data",
                table: "AuctionListing");

            migrationBuilder.DropIndex(
                name: "IX_AuctionListing_EscrowStorageId",
                schema: "data",
                table: "AuctionListing");

            migrationBuilder.DropColumn(
                name: "EscrowStorageId",
                schema: "data",
                table: "AuctionListing");
        }
    }
}
