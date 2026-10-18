export type Role = 'CorporatePlanning' | 'Planner' | 'DisciplineManager' | 'Viewer' | 'Admin';
export type CycleStatus = 'Draft' | 'PlannerEntry' | 'DisciplineReview' | 'Approval' | 'Implementation' | 'Locked';
export type Category = 'Running' | 'Internal' | 'LoanTo' | 'UnderSignature' | 'Proposal' | 'Potential';

export interface Me { upn: string; name: string; roles: Role[]; departments: string[]; authMode: string }

export interface Cycle {
  id: number; name: string; firstMonth: string; monthCount: number; status: CycleStatus;
  plannerDue?: string | null; reviewDue?: string | null; approvalDue?: string | null; hasAfterData: boolean;
  createdBy: string; lockedAt?: string | null;
}

export interface Proposal {
  id: number; lineId?: number | null; deptCode: string; kind: 'Change' | 'NewAllocation' | 'CategoryChange';
  status: 'Draft' | 'Submitted' | 'Approved' | 'PartiallyApproved' | 'Returned' | 'Rejected';
  reason: string; changeTypes?: string | null; requestedCategory?: Category | null; newProjectCode?: string | null;
  newActivity?: string | null; newCategory?: Category | null; newSectionCode?: string | null; createdBy: string;
  submittedAt?: string | null; reviewedBy?: string | null; reviewedAt?: string | null; reviewComment?: string | null;
  implementedAt?: string | null; implementedBy?: string | null; values: (number | null)[]; approved: (number | null)[];
}

export interface Line {
  id: number; dept: string; section: string; category: Category; categoryLabel: string; project: string; activity: string;
  planner?: string | null; origin: string; corporateControlled: boolean;
  planning: (number | null)[]; approved: (number | null)[]; after: (number | null)[];
  proposal: Proposal | null; categoryRequest: Proposal | null;
}

export interface Department {
  code: string; name: string; parentCode?: string | null; permanent: number; temporary: number; managers: number;
  loanToUnpaid: number; isActive: boolean; effectiveHeads: number; capacityPerMonth: number;
}

export interface Settings {
  permanentEffectiveness: number; hoursPerManMonth: number; bulkMaxPct: number; bulkMaxMH: number;
  justifyPct: number; justifyMH: number; justifyMinChars: number; reminderDaysBefore: number;
}

export interface DeptSummary {
  deptCode: string; department: string; reviewStatus: string; planning: number; discipline: number; final: number; after: number;
  grossIncrease: number; grossReduction: number; concernItems: number; capacityPerMonth: number; effectiveHeads: number;
  utilisationPlanning: number; utilisationDiscipline: number; utilisationFinal: number; firmUtilisationDiscipline: number;
  overloadMonths: number; implementationRate: number; net: number; deviationPct: number;
}

export interface ProjectSeverity {
  projectCode: string; name: string; client?: string | null; points: number; level: string; deptsRaising: number;
  departments: string; planning: number; discipline: number; grossIncrease: number; grossReduction: number;
  monthsAffected: number; hasNew: boolean; hasExtension: boolean; types: string; inMaster: boolean; net: number;
}

export interface Dashboard {
  cycle: Cycle;
  kpis: {
    planning: number; discipline: number; final: number; after: number; grossIncrease: number; grossReduction: number;
    concernItems: number; requested: number; implementationRate: number; newAllocations: number; pendingDecisions: number;
    critical: number; high: number; departmentsSubmitted: number; departments: number; capacityPerMonth: number;
  };
  bridge: { newWork: number; increase: number; extension: number; reduction: number; removed: number };
  monthly: { month: string; planning: number; discipline: number; final: number; after: number; capacity: number }[];
  departments: DeptSummary[];
  topProjects: ProjectSeverity[];
}

export interface QueueItem {
  proposal: Proposal; lineId?: number | null; dept: string; section: string; category: string; project: string; activity: string;
  planning: number[]; discipline: number[]; final: number[];
  change: { types: string[]; grossIncrease: number; grossReduction: number; net: number; extensionMH: number; briefCategories: string; monthsChanged: number[] };
  severity: string; points: number; projectDepartments?: string; projectName?: string; implementation: string;
}

export const CATEGORY_LABEL: Record<Category, string> = {
  Running: 'Running', Internal: 'Internal / Unforeseen', LoanTo: 'Loan To', UnderSignature: 'Under Signature',
  Proposal: 'Proposal', Potential: 'Potential Opportunity',
};
export const CATEGORY_ORDER: Category[] = ['Running', 'Internal', 'LoanTo', 'UnderSignature', 'Proposal', 'Potential'];
export const STAGES: CycleStatus[] = ['Draft', 'PlannerEntry', 'DisciplineReview', 'Approval', 'Implementation', 'Locked'];
export const STAGE_LABEL: Record<CycleStatus, string> = {
  Draft: 'Draft', PlannerEntry: 'Planner entry', DisciplineReview: 'Discipline review', Approval: 'Approval',
  Implementation: 'Implementation', Locked: 'Locked',
};
