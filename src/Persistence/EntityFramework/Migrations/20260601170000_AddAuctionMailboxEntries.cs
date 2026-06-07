using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(EntityDataContext))]
    [Migration("20260601170000_AddAuctionMailboxEntries")]
    public partial class AddAuctionMailboxEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuctionMailboxEntry",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ListingNumber = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemStorageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SenderCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemGroup = table.Column<byte>(type: "smallint", nullable: false),
                    ItemNumber = table.Column<short>(type: "smallint", nullable: false),
                    ItemLevel = table.Column<byte>(type: "smallint", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<int>(type: "integer", nullable: false),
                    JewelBankSlot = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionMailboxEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionMailboxEntry_Item_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "data",
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AuctionMailboxEntry_ItemStorage_ItemStorageId",
                        column: x => x.ItemStorageId,
                        principalSchema: "data",
                        principalTable: "ItemStorage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionMailboxEntry_ItemId",
                schema: "data",
                table: "AuctionMailboxEntry",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionMailboxEntry_ItemStorageId",
                schema: "data",
                table: "AuctionMailboxEntry",
                column: "ItemStorageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionMailboxEntry_ListingNumber",
                schema: "data",
                table: "AuctionMailboxEntry",
                column: "ListingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionMailboxEntry_OwnerCharacterId_ClaimedAt_Type",
                schema: "data",
                table: "AuctionMailboxEntry",
                columns: new[] { "OwnerCharacterId", "ClaimedAt", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuctionMailboxEntry",
                schema: "data");
        }
    }
}
