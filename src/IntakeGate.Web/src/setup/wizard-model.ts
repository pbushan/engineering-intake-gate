import type { OnboardingDraftValues, SetupStatus } from '../api/contracts';

export const setupSteps = [
  { id: 'Welcome', label: 'Welcome' },
  { id: 'AzureDevOps', label: 'Azure DevOps' },
  { id: 'Ai', label: 'AI provider' },
  { id: 'SystemDefaults', label: 'System defaults' },
  { id: 'Profile', label: 'Profile & policy' },
  { id: 'SavedQuery', label: 'Saved query' },
  { id: 'OptionalSchedule', label: 'Schedule' },
  { id: 'ReviewFinish', label: 'Review & finish' },
] as const;

export type SetupStepId = (typeof setupSteps)[number]['id'];
export type ScheduleChoice = 'manual' | 'hourly' | 'daily' | 'weekly' | 'custom';

export const schedulePresets: Record<Exclude<ScheduleChoice, 'custom'>, { enabled: boolean; expression: string }> = {
  manual: { enabled: false, expression: '' },
  hourly: { enabled: true, expression: '0 0 * * * *' },
  daily: { enabled: true, expression: '0 0 2 * * *' },
  weekly: { enabled: true, expression: '0 0 2 * * 1' },
};

const stepIndex = new Map<string, number>(setupSteps.map((step, index) => [step.id, index]));

export function resolveResumeStep(status: SetupStatus, actual: { adoSettingsReady?: boolean } = {}): number {
  const preferred = stepIndex.get(status.lastVisitedStep ?? '') ?? 0;
  let earliestIncomplete = setupSteps.length - 1;
  if (actual.adoSettingsReady === false || !status.azureDevOpsCredentialConfigured || !status.azureDevOpsCredentialVerified) earliestIncomplete = 1;
  else if (!status.aiCredentialConfigured || !status.aiCredentialVerified || !status.aiModelConfigured) earliestIncomplete = 2;
  else if (!status.onboardingDraftExists) earliestIncomplete = 3;
  else if (!status.profileDetailsComplete || !status.policyDetailsComplete) earliestIncomplete = 4;
  else if (!status.azureDevOpsSavedQueryConfirmed) earliestIncomplete = 5;
  return Math.min(preferred, earliestIncomplete);
}

export function scheduleChoice(values: OnboardingDraftValues | null): ScheduleChoice {
  const schedule = values?.schedule;
  if (!schedule?.enabled) return 'manual';
  const match = Object.entries(schedulePresets).find(([, preset]) =>
    preset.enabled === schedule.enabled && preset.expression === (schedule.expression ?? ''));
  return (match?.[0] as ScheduleChoice | undefined) ?? 'custom';
}

export function displayDate(value: string | null): string {
  if (!value) return 'Not yet';
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? 'Unavailable' : date.toLocaleString();
}

export function numeric(value: string): number | null {
  if (value.trim() === '') return null;
  const candidate = Number(value);
  return Number.isFinite(candidate) ? candidate : null;
}

export function cloneDraft(values: OnboardingDraftValues): OnboardingDraftValues {
  return structuredClone(values);
}
