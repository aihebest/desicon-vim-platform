using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Vim.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "vim");

            migrationBuilder.CreateSequence(
                name: "CaseReferenceSeq",
                schema: "vim");

            migrationBuilder.CreateTable(
                name: "Cases",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CaseType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SenderEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubmissionSubject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SubmissionRound = table.Column<int>(type: "int", nullable: false),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VendorNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    OrderNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SupplyType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CategoryCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExtractedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RegisteredById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedReviewerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewerDelegateIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    VerifiedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApProcessedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReminderAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReminderSent = table.Column<bool>(type: "bit", nullable: false),
                    EscalatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ErpDocumentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LastReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    LastAuditHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastAuditSequence = table.Column<int>(type: "int", nullable: false),
                    InvoiceNumberKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    VendorNameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CategoryRoutes",
                schema: "vim",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReviewerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DelegateIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryRoutes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "InboundMessages",
                schema: "vim",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CaseReference = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Template = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RouteChanges",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChangedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OldValueJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NewValueJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorRoles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "vim",
                        principalTable: "Cases",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CaseChecklistAnswers",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChecklistVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ItemCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Answer = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AnsweredById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AnsweredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseChecklistAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseChecklistAnswers_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "vim",
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaseDocuments",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    SubmissionRound = table.Column<int>(type: "int", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseDocuments_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "vim",
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaseFlags",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Blocking = table.Column<bool>(type: "bit", nullable: false),
                    RaisedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RaisedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseFlags_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "vim",
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_OccurredAtUtc",
                schema: "vim",
                table: "AuditEvents",
                columns: new[] { "Action", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CaseId_Sequence",
                schema: "vim",
                table: "AuditEvents",
                columns: new[] { "CaseId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseChecklistAnswers_CaseId",
                schema: "vim",
                table: "CaseChecklistAnswers",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocuments_CaseId",
                schema: "vim",
                table: "CaseDocuments",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseDocuments_Sha256",
                schema: "vim",
                table: "CaseDocuments",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_CaseFlags_CaseId_ResolvedAtUtc",
                schema: "vim",
                table: "CaseFlags",
                columns: new[] { "CaseId", "ResolvedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_AssignedReviewerId_Status",
                schema: "vim",
                table: "Cases",
                columns: new[] { "AssignedReviewerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_InvoiceNumber",
                schema: "vim",
                table: "Cases",
                column: "InvoiceNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_InvoiceNumberKey",
                schema: "vim",
                table: "Cases",
                column: "InvoiceNumberKey");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_Reference",
                schema: "vim",
                table: "Cases",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cases_Status_DueAtUtc",
                schema: "vim",
                table: "Cases",
                columns: new[] { "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_VendorName",
                schema: "vim",
                table: "Cases",
                column: "VendorName");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_VendorNameKey",
                schema: "vim",
                table: "Cases",
                column: "VendorNameKey");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_VendorNo",
                schema: "vim",
                table: "Cases",
                column: "VendorNo");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CaseId",
                schema: "vim",
                table: "OutboxMessages",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAtUtc_NextAttemptAtUtc",
                schema: "vim",
                table: "OutboxMessages",
                columns: new[] { "SentAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RouteChanges_RouteCode_ChangedAtUtc",
                schema: "vim",
                table: "RouteChanges",
                columns: new[] { "RouteCode", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "CaseChecklistAnswers",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "CaseDocuments",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "CaseFlags",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "CategoryRoutes",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "InboundMessages",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "RouteChanges",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "Cases",
                schema: "vim");

            migrationBuilder.DropSequence(
                name: "CaseReferenceSeq",
                schema: "vim");
        }
    }
}
