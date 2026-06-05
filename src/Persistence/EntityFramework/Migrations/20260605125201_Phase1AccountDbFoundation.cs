using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    public partial class Phase1AccountDbFoundation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DuelLosses",
                schema: "data",
                table: "Character",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DuelRating",
                schema: "data",
                table: "Character",
                type: "integer",
                nullable: false,
                defaultValue: 1200);

            migrationBuilder.AddColumn<byte>(
                name: "DuelResetBracket",
                schema: "data",
                table: "Character",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "DuelWins",
                schema: "data",
                table: "Character",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "MuHelperConfiguration",
                schema: "data",
                table: "Character",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankBless",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankChaos",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankChocoBlue",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankChocoPink",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankCreation",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankGemstone",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankGuardian",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankHarmony",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankHigherRefineStone",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankKundun1",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankKundun2",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankKundun3",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankKundun4",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankKundun5",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankLife",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankLowerRefineStone",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "JewelBankSoul",
                schema: "data",
                table: "Account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VipExpirationDate",
                schema: "data",
                table: "Account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WCoin",
                schema: "data",
                table: "Account",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AuctionListing",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EscrowItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    EscrowStorageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ListingNumber = table.Column<long>(type: "bigint", nullable: false),
                    SellerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BuyerAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    BuyerCharacterId = table.Column<Guid>(type: "uuid", nullable: true),
                    BuyerCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ItemGroup = table.Column<byte>(type: "smallint", nullable: false),
                    ItemNumber = table.Column<short>(type: "smallint", nullable: false),
                    ItemLevel = table.Column<byte>(type: "smallint", nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false),
                    FeeAmount = table.Column<long>(type: "bigint", nullable: false),
                    SellerPayoutAmount = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<int>(type: "integer", nullable: false),
                    JewelBankSlot = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveryClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SellerPayoutClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionListing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionListing_Item_EscrowItemId",
                        column: x => x.EscrowItemId,
                        principalSchema: "data",
                        principalTable: "Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AuctionListing_ItemStorage_EscrowStorageId",
                        column: x => x.EscrowStorageId,
                        principalSchema: "data",
                        principalTable: "ItemStorage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuctionMailboxEntry",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemStorageId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ListingNumber = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ItemDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SenderCharacterName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemGroup = table.Column<byte>(type: "smallint", nullable: false),
                    ItemNumber = table.Column<short>(type: "smallint", nullable: false),
                    ItemLevel = table.Column<byte>(type: "smallint", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<int>(type: "integer", nullable: false),
                    JewelBankSlot = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "WCoinTransaction",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfter = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WCoinTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WCoinTransaction_Account_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "data",
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_BuyerCharacterId_Status",
                schema: "data",
                table: "AuctionListing",
                columns: new[] { "BuyerCharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_EscrowItemId",
                schema: "data",
                table: "AuctionListing",
                column: "EscrowItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_EscrowStorageId",
                schema: "data",
                table: "AuctionListing",
                column: "EscrowStorageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_ListingNumber",
                schema: "data",
                table: "AuctionListing",
                column: "ListingNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_SellerCharacterId_Status",
                schema: "data",
                table: "AuctionListing",
                columns: new[] { "SellerCharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionListing_Status_ExpiresAt",
                schema: "data",
                table: "AuctionListing",
                columns: new[] { "Status", "ExpiresAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_WCoinTransaction_AccountId_Timestamp",
                schema: "data",
                table: "WCoinTransaction",
                columns: new[] { "AccountId", "Timestamp" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuctionListing",
                schema: "data");

            migrationBuilder.DropTable(
                name: "AuctionMailboxEntry",
                schema: "data");

            migrationBuilder.DropTable(
                name: "WCoinTransaction",
                schema: "data");

            migrationBuilder.DropColumn(
                name: "DuelLosses",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "DuelRating",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "DuelResetBracket",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "DuelWins",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "MuHelperConfiguration",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "JewelBankBless",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankChaos",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankChocoBlue",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankChocoPink",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankCreation",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankGemstone",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankGuardian",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankHarmony",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankHigherRefineStone",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankKundun1",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankKundun2",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankKundun3",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankKundun4",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankKundun5",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankLife",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankLowerRefineStone",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "JewelBankSoul",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "VipExpirationDate",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "WCoin",
                schema: "data",
                table: "Account");
        }
    }
}
