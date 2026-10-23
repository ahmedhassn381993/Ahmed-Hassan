using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enppi.Workload.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    User = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Entity = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CycleId = table.Column<int>(type: "int", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cycles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FirstMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    MonthCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlannerDue = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewDue = table.Column<DateOnly>(type: "date", nullable: true),
                    ApprovalDue = table.Column<DateOnly>(type: "date", nullable: true),
                    HasAfterData = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LockedCapacity = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DepartmentReviews",
                columns: table => new
                {
                    CycleId = table.Column<int>(type: "int", nullable: false),
                    DeptCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LastReminderAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderCount = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentReviews", x => new { x.CycleId, x.DeptCode });
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ParentCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Permanent = table.Column<int>(type: "int", nullable: false),
                    Temporary = table.Column<int>(type: "int", nullable: false),
                    Managers = table.Column<int>(type: "int", nullable: false),
                    LoanToUnpaid = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CycleId = table.Column<int>(type: "int", nullable: false),
                    DeptCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Sent = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BaseCode = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Client = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    MasterCategory = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    InMaster = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Upn = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeptCodes = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Upn);
                });

            migrationBuilder.CreateTable(
                name: "Lines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CycleId = table.Column<int>(type: "int", nullable: false),
                    DeptCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    SectionCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovedCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AfterCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProjectCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Activity = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    PlannerUpn = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lines_Cycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "Cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Proposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CycleId = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: true),
                    DeptCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ChangeTypes = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    RequestedCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    NewProjectCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    NewActivity = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    NewCategory = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    NewSectionCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImplementedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ImplementedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Proposals_Cycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "Cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Proposals_Lines_LineId",
                        column: x => x.LineId,
                        principalTable: "Lines",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Values",
                columns: table => new
                {
                    LineId = table.Column<int>(type: "int", nullable: false),
                    MonthIndex = table.Column<int>(type: "int", nullable: false),
                    PlannerMH = table.Column<double>(type: "float", nullable: true),
                    ApprovedMH = table.Column<double>(type: "float", nullable: true),
                    AfterMH = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Values", x => new { x.LineId, x.MonthIndex });
                    table.ForeignKey(
                        name: "FK_Values_Lines_LineId",
                        column: x => x.LineId,
                        principalTable: "Lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProposalValues",
                columns: table => new
                {
                    ProposalId = table.Column<int>(type: "int", nullable: false),
                    MonthIndex = table.Column<int>(type: "int", nullable: false),
                    ProposedMH = table.Column<double>(type: "float", nullable: true),
                    ApprovedMH = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProposalValues", x => new { x.ProposalId, x.MonthIndex });
                    table.ForeignKey(
                        name: "FK_ProposalValues_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_CycleId_At",
                table: "Audit",
                columns: new[] { "CycleId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Cycles_Name",
                table: "Cycles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lines_CycleId_DeptCode",
                table: "Lines",
                columns: new[] { "CycleId", "DeptCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Lines_CycleId_ProjectCode",
                table: "Lines",
                columns: new[] { "CycleId", "ProjectCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CycleId_DeptCode",
                table: "Notifications",
                columns: new[] { "CycleId", "DeptCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_CycleId_DeptCode",
                table: "Proposals",
                columns: new[] { "CycleId", "DeptCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_CycleId_Status",
                table: "Proposals",
                columns: new[] { "CycleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_LineId",
                table: "Proposals",
                column: "LineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "DepartmentReviews");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "ProposalValues");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Values");

            migrationBuilder.DropTable(
                name: "Proposals");

            migrationBuilder.DropTable(
                name: "Lines");

            migrationBuilder.DropTable(
                name: "Cycles");
        }
    }
}
