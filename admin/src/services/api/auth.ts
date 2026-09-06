import { apiClient } from "./client";
import type {
  AuthBffResponse,
  AuthUser,
  LoginRequest,
  RegisterRequest,
} from "@/types/api";

export interface AcceptTenantInvitationRequest {
  token: string;
  password: string;
  identificationType: string;
  identification: string;
  firstName: string;
  lastName: string;
  username: string;
  phone: string;
  address: string;
  passwordConfirmation: string;
}

export interface AcceptTenantInvitationResult {
  tenantId: string;
  userId: string;
  username: string;
  tenantKey: string;
  email: string;
  status: string;
}

export const authApi = {
  login: (data: LoginRequest) =>
    apiClient.post<AuthBffResponse>("/auth/login", data),
  register: (data: RegisterRequest) =>
    apiClient.post<AuthBffResponse>("/auth/register", data),
  acceptInvitation: (data: AcceptTenantInvitationRequest) =>
    apiClient.post<AcceptTenantInvitationResult>("/auth/invitations/accept", data),
  changePassword: (currentPassword: string, newPassword: string) =>
    apiClient.post<void>("/auth/change-password", { currentPassword, newPassword }),
  me: () => apiClient.get<AuthUser>("/auth/me"),
};
