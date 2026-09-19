import type { components } from './generated/schema';

export type ApiErrorResponse = components['schemas']['ApiErrorResponse'];
export type AiCredentialTest = components['schemas']['AiCredentialTestResponse'];
export type AiModelCandidate = components['schemas']['ValidateAiModelResponse'];
export type AiModelConfirmation = components['schemas']['ConfirmAiModelResponse'];
export type AiModelDiscovery = components['schemas']['AiModelDiscoveryResponse'];
export type AiModelDescriptor = components['schemas']['AiModelDescriptor'];
export type AiSettings = components['schemas']['AiSettingsResponse'];
export type AiModelPricing = components['schemas']['AiModelPricingResponse'];
export type AzureDevOpsConnectionTest = components['schemas']['ConnectionTestResponse'];
export type AzureDevOpsQueryCandidate = components['schemas']['ValidateSavedQueryResponse'];
export type AzureDevOpsQueryConfirmation = components['schemas']['ConfirmSavedQueryResponse'];
export type AzureDevOpsSettings = components['schemas']['AzureDevOpsSettingsResponse'];
export type BootstrapRequest = components['schemas']['BootstrapRequest'];
export type BootstrapStatus = components['schemas']['BootstrapStatusResponse'];
export type CredentialMetadata = components['schemas']['CredentialMetadataResponse'];
export type LoginRequest = components['schemas']['LoginRequest'];
export type OnboardingDefaults = components['schemas']['OnboardingDefaults'];
export type OnboardingDraftState = components['schemas']['OnboardingDraftStateResponse'];
export type OnboardingDraftUpdate = components['schemas']['OnboardingDraftUpdateRequest'];
export type OnboardingDraftValues = components['schemas']['OnboardingProfileDraftValues'];
export type AnalyzeWorkItemRequest = components['schemas']['AnalyzeWorkItemRequest'];
export type AnalyzeWorkItemResponse = components['schemas']['AnalyzeWorkItemResponse'];
export type RunExecution = components['schemas']['RunExecutionResponse'];
export type RunHistoryPage = components['schemas']['RunHistoryPageResponse'];
export type RunSummary = components['schemas']['RunSummaryResponse'];
export type RunDetail = components['schemas']['RunDetailResponse'];
export type RunItemSummary = components['schemas']['RunItemSummaryResponse'];
export type RunItemDetail = components['schemas']['RunItemDetailResponse'];
export type OperationalDecision = components['schemas']['OperationalDecisionState'];
export type OperationalRunStatus = components['schemas']['OperationalRunStatus'];
export type RunTriggerType = components['schemas']['RunTriggerType'];
export type TokenUsage = components['schemas']['TokenUsageResponse'];
export type EstimatedCost = components['schemas']['EstimatedCostResponse'];
export type Effect = components['schemas']['EffectResponse'];
export type SafeOperationalError = components['schemas']['SafeOperationalErrorResponse'];
export type SystemHealth = components['schemas']['SystemHealthResponse'];
export type HomeSummary = components['schemas']['HomeSummaryResponse'];
export type HomeHealthWarning = components['schemas']['HomeHealthWarningResponse'];
export type RecentFailure = components['schemas']['RecentFailureResponse'];
export type ControlPlaneAuditPage = components['schemas']['ControlPlaneAuditPageResponse'];
export type ControlPlaneAuditItem = components['schemas']['ControlPlaneAuditItemResponse'];
export type ProfileState = components['schemas']['ProfileStateResponse'];
export type ProfileUpdate = components['schemas']['ProfileUpdateRequest'];
export type SafeUser = components['schemas']['SafeUserResponse'];
export type SetupStatus = components['schemas']['SetupStatusResponse'];
export type SetupFinalizeRequest = components['schemas']['SetupFinalizeRequest'];
export type SetupProgressRequest = components['schemas']['SetupProgressRequest'];
export type SetupProgress = components['schemas']['SetupProgressResponse'];
export type VersionInfo = components['schemas']['VersionResponse'];

export type UserRole = SafeUser['role'];
export type AiProvider = 'openai' | 'anthropic';
export type HomeWindowDays = 7 | 30 | 90;

export interface PortableProfileDocument {
  format: 'engineering-intake-gate-profile';
  version: 1;
  exportedAt: string;
  profile: Omit<NonNullable<OnboardingDraftValues>, 'policy'>;
  policy: NonNullable<NonNullable<OnboardingDraftValues>['policy']>;
}

export interface ProfileImportPreview {
  document: PortableProfileDocument;
  complete: boolean;
  fieldErrors: Record<string, string[]>;
  sectionErrors: Record<string, string[]>;
}

export interface ProfileImportResult {
  profileReplaced: boolean;
  profile: ProfileState | null;
  draft: OnboardingDraftState | null;
}
