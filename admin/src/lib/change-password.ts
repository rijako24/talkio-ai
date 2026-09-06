export interface ChangePasswordValues {
  currentPassword: string;
  newPassword: string;
  passwordConfirmation: string;
}

export function validateChangePassword(values: ChangePasswordValues): string | null {
  if (!values.currentPassword) return "Escribe tu contraseña actual.";
  if (values.newPassword.length < 10) {
    return "La nueva contraseña debe tener al menos 10 caracteres.";
  }
  if (values.newPassword !== values.passwordConfirmation) {
    return "La confirmación de la contraseña no coincide.";
  }
  return null;
}
