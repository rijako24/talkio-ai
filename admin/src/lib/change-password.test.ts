import assert from "node:assert/strict";
import test from "node:test";

import { validateChangePassword } from "./change-password";

test("requires the current password", () => {
  assert.equal(validateChangePassword({
    currentPassword: "",
    newPassword: "nueva-clave-segura",
    passwordConfirmation: "nueva-clave-segura",
  }), "Escribe tu contraseña actual.");
});

test("requires at least ten characters in the new password", () => {
  assert.equal(validateChangePassword({
    currentPassword: "clave-anterior",
    newPassword: "corta",
    passwordConfirmation: "corta",
  }), "La nueva contraseña debe tener al menos 10 caracteres.");
});

test("requires matching confirmation", () => {
  assert.equal(validateChangePassword({
    currentPassword: "clave-anterior",
    newPassword: "nueva-clave-segura",
    passwordConfirmation: "otra-clave-segura",
  }), "La confirmación de la contraseña no coincide.");
});

test("accepts a valid password change", () => {
  assert.equal(validateChangePassword({
    currentPassword: "clave-anterior",
    newPassword: "nueva-clave-segura",
    passwordConfirmation: "nueva-clave-segura",
  }), null);
});
