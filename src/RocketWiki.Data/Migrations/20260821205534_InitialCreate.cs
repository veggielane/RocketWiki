using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RocketWiki.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttributeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClaimName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    AllowedValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnownGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncImportStates",
                columns: table => new
                {
                    OriginInstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastBundleNumber = table.Column<int>(type: "int", nullable: false),
                    LastManifestHash = table.Column<string>(type: "char(64)", nullable: false),
                    LastImportAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncImportStates", x => x.OriginInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "SyncSpaceStates",
                columns: table => new
                {
                    OriginInstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppliedSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSpaceStates", x => new { x.OriginInstanceId, x.SpaceId });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsExternal = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "varchar(64)", nullable: false),
                    SubjectType = table.Column<byte>(type: "tinyint", nullable: true),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SpaceKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: false),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestId = table.Column<string>(type: "varchar(64)", nullable: false),
                    ClientIp = table.Column<string>(type: "varchar(45)", nullable: false),
                    McpClient = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => new { x.TimestampUtc, x.Id });
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AccessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<byte>(type: "tinyint", nullable: true),
                    Action = table.Column<byte>(type: "tinyint", nullable: true),
                    ExpressionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRules", x => x.Id);
                    table.CheckConstraint("CK_AccessRules_KindColumnPairing", "([Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NOT NULL AND [Action] IS NULL) OR ([Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL AND [Action] IS NOT NULL AND [Role] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EditedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_Comments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "Comments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Labels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TitleSnapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Notifications_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PageEmbeddings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    HeadingPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ChunkHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Embedding = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PageLabels",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LabelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageLabels", x => new { x.PageId, x.LabelId });
                    table.ForeignKey(
                        name: "FK_PageLabels_Labels_LabelId",
                        column: x => x.LabelId,
                        principalTable: "Labels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PageRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EditSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageRevisions_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Pages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AncestorPath = table.Column<string>(type: "nvarchar(2600)", maxLength: 2600, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CurrentRevisionNumber = table.Column<int>(type: "int", nullable: false),
                    CurrentContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeleteBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pages_Pages_ParentPageId",
                        column: x => x.ParentPageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Spaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HomepageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginInstanceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsExported = table.Column<bool>(type: "bit", nullable: false),
                    LastOutboxSequence = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Spaces_Pages_HomepageId",
                        column: x => x.HomepageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncOutboxEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<byte>(type: "tinyint", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExportedInBundle = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutboxEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncOutboxEvents_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Watches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watches", x => x.Id);
                    table.CheckConstraint("CK_Watches_SpaceXorPage", "([SpaceId] IS NOT NULL AND [PageId] IS NULL) OR ([SpaceId] IS NULL AND [PageId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Watches_Pages_PageId",
                        column: x => x.PageId,
                        principalTable: "Pages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Watches_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Watches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRules_PageId",
                table: "AccessRules",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRules_SpaceId",
                table: "AccessRules",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ContentHash",
                table: "Attachments",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_PageId",
                table: "Attachments",
                column: "PageId",
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploadedByUserId",
                table: "Attachments",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitions_Key",
                table: "AttributeDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "Action", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SubjectId_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "SubjectId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_UserId_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "UserId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorUserId",
                table: "Comments",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_PageId_CreatedAtUtc",
                table: "Comments",
                columns: new[] { "PageId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentCommentId",
                table: "Comments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_KnownGroups_Name",
                table: "KnownGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Labels_SpaceId_Name",
                table: "Labels",
                columns: new[] { "SpaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ActorUserId",
                table: "Notifications",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_PageId",
                table: "Notifications",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Recipient_All",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Recipient_Unread",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "CreatedAtUtc" },
                filter: "[ReadAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SpaceId",
                table: "Notifications",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PageEmbeddings_PageId_ChunkIndex",
                table: "PageEmbeddings",
                columns: new[] { "PageId", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PageLabels_LabelId",
                table: "PageLabels",
                column: "LabelId");

            migrationBuilder.CreateIndex(
                name: "IX_PageRevisions_AuthorUserId",
                table: "PageRevisions",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PageRevisions_PageId_RevisionNumber",
                table: "PageRevisions",
                columns: new[] { "PageId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pages_AncestorPath",
                table: "Pages",
                column: "AncestorPath");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_DeleteBatchId",
                table: "Pages",
                column: "DeleteBatchId",
                filter: "[DeleteBatchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_ParentPageId",
                table: "Pages",
                column: "ParentPageId");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Space_Parent_Slug",
                table: "Pages",
                columns: new[] { "SpaceId", "ParentPageId", "Slug" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Space_Parent_Sort",
                table: "Pages",
                columns: new[] { "SpaceId", "ParentPageId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_HomepageId",
                table: "Spaces",
                column: "HomepageId");

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_Key",
                table: "Spaces",
                column: "Key",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutboxEvents_SpaceId_SequenceNumber",
                table: "SyncOutboxEvents",
                columns: new[] { "SpaceId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Subject",
                table: "Users",
                column: "Subject",
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_PageId",
                table: "Watches",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_SpaceId",
                table: "Watches",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_User_Page",
                table: "Watches",
                columns: new[] { "UserId", "PageId" },
                unique: true,
                filter: "[PageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_User_Space",
                table: "Watches",
                columns: new[] { "UserId", "SpaceId" },
                unique: true,
                filter: "[SpaceId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRules_Pages_PageId",
                table: "AccessRules",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AccessRules_Spaces_SpaceId",
                table: "AccessRules",
                column: "SpaceId",
                principalTable: "Spaces",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Pages_PageId",
                table: "Attachments",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Pages_PageId",
                table: "Comments",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Labels_Spaces_SpaceId",
                table: "Labels",
                column: "SpaceId",
                principalTable: "Spaces",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Pages_PageId",
                table: "Notifications",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Spaces_SpaceId",
                table: "Notifications",
                column: "SpaceId",
                principalTable: "Spaces",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PageEmbeddings_Pages_PageId",
                table: "PageEmbeddings",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PageLabels_Pages_PageId",
                table: "PageLabels",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PageRevisions_Pages_PageId",
                table: "PageRevisions",
                column: "PageId",
                principalTable: "Pages",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Pages_Spaces_SpaceId",
                table: "Pages",
                column: "SpaceId",
                principalTable: "Spaces",
                principalColumn: "Id");

            // design.md §9.1 / data-model.md: full-text index over (Title,
            // CurrentContent), keyed on the existing PK_Pages unique index (FULLTEXT
            // INDEX requires one). Not part of the EF relational model, so it's raw SQL
            // here rather than a fluent-API call. This statement only ever runs when
            // this migration is actually applied to a real SQL Server database - the
            // SQLite test tier builds its schema from the live model via
            // EnsureCreated() and never replays migrations, so ISearchService's LIKE
            // fallback (RocketWiki.Data.Services.SearchService) is what that tier
            // actually exercises. tests/RocketWiki.SqlServer.Tests (design.md §14's
            // third tier) replays this migration against a real, FTS-enabled SQL
            // Server container and asserts the index exists via sys.fulltext_indexes.
            //
            // suppressTransaction: EF Core runs each migration inside a transaction by
            // default, and SQL Server rejects CREATE FULLTEXT CATALOG / CREATE FULLTEXT
            // INDEX inside a user transaction (error 574: "... statement cannot be used
            // inside a user transaction"). These two statements therefore run after the
            // migration's transactional batch has committed. That is safe here: they are
            // the final operations of the migration, purely additive, and no database
            // had ever had this migration applied when the flag was added (README /
            // design.md §16's standing caveat - the fix itself is only provable by the
            // container tier that motivated it). Split into two calls so a failure
            // reports which statement died.
            migrationBuilder.Sql(
                "CREATE FULLTEXT CATALOG PageSearchCatalog AS DEFAULT;",
                suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE FULLTEXT INDEX ON Pages(Title, CurrentContent)
                    KEY INDEX PK_Pages ON PageSearchCatalog
                    WITH STOPLIST = SYSTEM, CHANGE_TRACKING AUTO;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Same transaction restriction as Up: FULLTEXT DDL cannot run inside the
            // migration's transaction. Index before catalog - a catalog with an index
            // still attached cannot be dropped.
            migrationBuilder.Sql("DROP FULLTEXT INDEX ON Pages;", suppressTransaction: true);
            migrationBuilder.Sql("DROP FULLTEXT CATALOG PageSearchCatalog;", suppressTransaction: true);

            migrationBuilder.DropForeignKey(
                name: "FK_Spaces_Pages_HomepageId",
                table: "Spaces");

            migrationBuilder.DropTable(
                name: "AccessRules");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AttributeDefinitions");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "KnownGroups");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PageEmbeddings");

            migrationBuilder.DropTable(
                name: "PageLabels");

            migrationBuilder.DropTable(
                name: "PageRevisions");

            migrationBuilder.DropTable(
                name: "SyncImportStates");

            migrationBuilder.DropTable(
                name: "SyncOutboxEvents");

            migrationBuilder.DropTable(
                name: "SyncSpaceStates");

            migrationBuilder.DropTable(
                name: "Watches");

            migrationBuilder.DropTable(
                name: "Labels");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Pages");

            migrationBuilder.DropTable(
                name: "Spaces");
        }
    }
}
