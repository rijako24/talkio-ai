"use client";

import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, MailPlus, MonitorSmartphone, Save, ShieldOff, UserRoundCheck, UsersRound } from "lucide-react";
import { toast } from "sonner";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useUsers } from "@/hooks/use-users";
import { tenantKeys } from "@/hooks/use-tenants";
import { formatDateTime } from "@/lib/utils";
import { tenantsApi, type TenantEnrolledDevice } from "@/services/api/tenants";
import { usersApi } from "@/services/api/users";
import { useAuthStore } from "@/stores/auth-store";
import type { AppUser, Tenant } from "@/types/entities";

export function TenantGovernancePanel({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const permissions = useAuthStore((state) => new Set(state.user?.permissions ?? []));
  const canUpdateCapacity = permissions.has("tenants.capacity.update");
  const canUpdateStatus = permissions.has("tenants.status.update");
  const canReadUsers = permissions.has("users.read");
  const canCreateUsers = permissions.has("users.create");
  const canChangeUserState = permissions.has("users.delete");
  const canReadDevices = permissions.has("tenants.devices.read");
  const canRevokeDevices = permissions.has("tenants.devices.revoke");
  const users = useUsers({ tenantId: tenant.tenantId, page: 1, pageSize: 100 }, { enabled: canReadUsers });
  const devices = useQuery({
    queryKey: ["tenants", tenant.tenantId, "devices"],
    queryFn: () => tenantsApi.listDevices(tenant.tenantId),
    enabled: canReadDevices,
  });
  const [maximumUsers, setMaximumUsers] = useState(tenant.maximumUsers);
  const [maximumDevices, setMaximumDevices] = useState(tenant.maximumEnrolledDevices);
  const [confirmTenantState, setConfirmTenantState] = useState(false);
  const [selectedUser, setSelectedUser] = useState<AppUser | null>(null);
  const [selectedDevice, setSelectedDevice] = useState<TenantEnrolledDevice | null>(null);

  useEffect(() => {
    setMaximumUsers(tenant.maximumUsers);
    setMaximumDevices(tenant.maximumEnrolledDevices);
  }, [tenant.maximumUsers, tenant.maximumEnrolledDevices]);

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: tenantKeys.detail(tenant.tenantId) }),
      queryClient.invalidateQueries({ queryKey: ["users"] }),
      queryClient.invalidateQueries({ queryKey: ["tenants", tenant.tenantId, "devices"] }),
    ]);
  };

  const updateCapacity = useMutation({
    mutationFn: () => tenantsApi.update(tenant.tenantId, { maximumUsers, maximumEnrolledDevices: maximumDevices }),
    onSuccess: async () => { toast.success("Capacidad actualizada"); await refresh(); },
    onError: () => toast.error("No fue posible actualizar la capacidad."),
  });
  const changeTenantState = useMutation({
    mutationFn: () => tenant.isActive ? tenantsApi.deactivate(tenant.tenantId) : tenantsApi.activate(tenant.tenantId),
    onSuccess: async () => { setConfirmTenantState(false); toast.success(tenant.isActive ? "Organización inactivada" : "Organización activada"); await refresh(); },
    onError: () => toast.error("No fue posible cambiar el estado de la organización."),
  });
  const changeUserState = useMutation({
    mutationFn: (user: AppUser) => user.isActive ? usersApi.deactivate(user.userId) : usersApi.activate(user.userId),
    onSuccess: async (_, user) => { setSelectedUser(null); toast.success(user.isActive ? "Usuario inactivado" : "Usuario activado"); await refresh(); },
    onError: () => toast.error("No fue posible cambiar el estado del usuario."),
  });
  const deactivateDevice = useMutation({
    mutationFn: (device: TenantEnrolledDevice) => tenantsApi.deactivateDevice(tenant.tenantId, device.deviceId),
    onSuccess: async () => { setSelectedDevice(null); toast.success("Caja desenrolada", { description: "El cupo quedó disponible para enrolar otra caja." }); await refresh(); },
    onError: () => toast.error("No fue posible desenrolar la caja."),
  });
  const resendInvitation = useMutation({
    mutationFn: () => tenantsApi.resendAdministratorInvitation(tenant.tenantId),
    onSuccess: (result) => toast.success("Invitación reenviada", {
      description: `Enviada a ${result.deliveryEmail}. El enlace estará activo durante 3 días.`,
    }),
    onError: (failure) => toast.error(
      failure instanceof Error ? failure.message : "No fue posible reenviar la invitación."),
  });

  const overUsers = tenant.activeUserCount > tenant.maximumUsers;
  const overDevices = tenant.activeEnrolledDeviceCount > tenant.maximumEnrolledDevices;
  const userItems = useMemo(() => users.data?.items ?? [], [users.data]);

  return <div className="space-y-5">
    <section className="rounded-2xl border bg-card p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-primary">Licenciamiento</p>
          <h2 className="mt-1 text-xl font-semibold">Capacidad de la organización</h2>
          <p className="mt-1 text-sm text-muted-foreground">Los límites son visibles; su edición requiere autorización expresa de plataforma.</p>
        </div>
        {canUpdateStatus && <Button variant={tenant.isActive ? "destructive" : "default"} onClick={() => setConfirmTenantState(true)}>
          {tenant.isActive ? <ShieldOff className="mr-2 h-4 w-4" /> : <UserRoundCheck className="mr-2 h-4 w-4" />}
          {tenant.isActive ? "Inactivar organización" : "Activar organización"}
        </Button>}
      </div>
      {(overUsers || overDevices) && <div className="mt-5 flex gap-3 rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" />
        <p>La organización está en sobrecupo. No se permiten nuevas altas, reactivaciones ni enrolamientos hasta volver al límite.</p>
      </div>}
      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        <CapacityCard icon={UsersRound} title="Usuarios activos" used={tenant.activeUserCount} allowed={tenant.maximumUsers} />
        <CapacityCard icon={MonitorSmartphone} title="Cajas enroladas" used={tenant.activeEnrolledDeviceCount} allowed={tenant.maximumEnrolledDevices} />
      </div>
      {canUpdateCapacity && <div className="mt-5 grid gap-4 rounded-xl border bg-muted/20 p-4 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
        <div className="space-y-2"><Label htmlFor="maximum-users">Usuarios activos permitidos</Label><Input id="maximum-users" inputMode="numeric" value={maximumUsers} onChange={(event) => setMaximumUsers(Number(event.target.value.replace(/\D/g, "")))} /></div>
        <div className="space-y-2"><Label htmlFor="maximum-devices">Cajas enroladas permitidas</Label><Input id="maximum-devices" inputMode="numeric" value={maximumDevices} onChange={(event) => setMaximumDevices(Number(event.target.value.replace(/\D/g, "")))} /></div>
        <Button disabled={maximumUsers < 1 || maximumDevices < 0 || updateCapacity.isPending} onClick={() => updateCapacity.mutate()}><Save className="mr-2 h-4 w-4" />Guardar límites</Button>
      </div>}
    </section>

    {canReadUsers && <section className="rounded-2xl border bg-card p-6 shadow-sm">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between"><div><p className="text-xs font-semibold uppercase tracking-wide text-primary">Acceso</p><h2 className="mt-1 text-xl font-semibold">Usuarios de la empresa</h2><p className="mt-1 text-sm text-muted-foreground">Se reutilizan los permisos de la vista Usuarios dentro de la empresa seleccionada.</p></div>{canCreateUsers && tenant.activeUserCount === 0 && <Button type="button" variant="outline" disabled={resendInvitation.isPending} onClick={() => resendInvitation.mutate()}><MailPlus className="mr-2 h-4 w-4" />{resendInvitation.isPending ? "Reenviando…" : "Reenviar invitación"}</Button>}</div>
      <div className="mt-5 overflow-hidden rounded-xl border">
        <div className="grid grid-cols-[minmax(0,1fr)_auto_auto] gap-3 bg-muted/50 px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground"><span>Usuario</span><span>Estado</span><span>Acción</span></div>
        {userItems.map((user) => <div key={user.userId} className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-3 border-t px-4 py-3">
          <span className="min-w-0"><strong className="block truncate text-sm">{user.firstName} {user.lastName}</strong><small className="block truncate text-muted-foreground">{user.email} · {user.username}</small></span>
          <Badge variant={user.isActive ? "default" : "secondary"}>{user.isActive ? "Activo" : "Inactivo"}</Badge>
          {canChangeUserState ? <Button size="sm" variant="outline" onClick={() => setSelectedUser(user)}>{user.isActive ? "Inactivar" : "Activar"}</Button> : <span />}
        </div>)}
        {!users.isLoading && userItems.length === 0 && <p className="border-t p-8 text-center text-sm text-muted-foreground">Sin datos</p>}
      </div>
    </section>}

    {canReadDevices && <section className="rounded-2xl border bg-card p-6 shadow-sm">
      <div><p className="text-xs font-semibold uppercase tracking-wide text-primary">Cajas</p><h2 className="mt-1 text-xl font-semibold">Cajas enroladas</h2><p className="mt-1 text-sm text-muted-foreground">El desenrolamiento requiere un permiso adicional al de consulta.</p></div>
      <div className="mt-5 overflow-hidden rounded-xl border">
        <div className="grid grid-cols-[minmax(0,1fr)_minmax(9rem,.6fr)_auto_auto] gap-3 bg-muted/50 px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground"><span>Caja</span><span>Sede</span><span>Última conexión</span><span>Acción</span></div>
        {(devices.data ?? []).map((device) => <div key={device.deviceId} className="grid grid-cols-[minmax(0,1fr)_minmax(9rem,.6fr)_auto_auto] items-center gap-3 border-t px-4 py-3">
          <span className="min-w-0"><strong className="block truncate text-sm">{device.name}</strong><small className="block truncate text-muted-foreground">{device.deviceId}</small></span>
          <span className="text-sm">{device.businessName ?? "Sin sede"}</span>
          <span className="text-sm text-muted-foreground">{device.lastSeenAt ? formatDateTime(device.lastSeenAt) : "Sin conexión"}</span>
          {device.isActive && canRevokeDevices ? <Button size="sm" variant="outline" onClick={() => setSelectedDevice(device)}>Desenrolar</Button> : <Badge variant="secondary">{device.isActive ? "Activa" : "Desenrolada"}</Badge>}
        </div>)}
        {!devices.isLoading && (devices.data ?? []).length === 0 && <p className="border-t p-8 text-center text-sm text-muted-foreground">Sin datos</p>}
      </div>
    </section>}

    {canUpdateStatus && <Dialog open={confirmTenantState} onOpenChange={setConfirmTenantState}>
      <DialogContent><DialogHeader><DialogTitle>{tenant.isActive ? "¿Inactivar esta organización?" : "¿Activar esta organización?"}</DialogTitle><DialogDescription>{tenant.isActive ? "Se bloquearán nuevos inicios de sesión y se revocarán las sesiones actuales. Los datos permanecen para auditoría." : "Los usuarios activos podrán volver a iniciar sesión."}</DialogDescription></DialogHeader><DialogFooter><Button variant="outline" onClick={() => setConfirmTenantState(false)}>Cancelar</Button><Button variant={tenant.isActive ? "destructive" : "default"} disabled={changeTenantState.isPending} onClick={() => changeTenantState.mutate()}>{tenant.isActive ? "Sí, inactivar" : "Sí, activar"}</Button></DialogFooter></DialogContent>
    </Dialog>}
    {canRevokeDevices && <Dialog open={!!selectedDevice} onOpenChange={(open) => !open && setSelectedDevice(null)}>
      <DialogContent><DialogHeader><DialogTitle>¿Desenrolar esta caja?</DialogTitle><DialogDescription>La caja dejará de sincronizar, sus sesiones se cerrarán y el cupo quedará disponible. Los documentos permanecen para auditoría.</DialogDescription></DialogHeader><DialogFooter><Button variant="outline" onClick={() => setSelectedDevice(null)}>Cancelar</Button><Button variant="destructive" disabled={!selectedDevice || deactivateDevice.isPending} onClick={() => selectedDevice && deactivateDevice.mutate(selectedDevice)}>Sí, desenrolar</Button></DialogFooter></DialogContent>
    </Dialog>}
    {canChangeUserState && <Dialog open={!!selectedUser} onOpenChange={(open) => !open && setSelectedUser(null)}>
      <DialogContent><DialogHeader><DialogTitle>{selectedUser?.isActive ? "¿Inactivar este usuario?" : "¿Activar este usuario?"}</DialogTitle><DialogDescription>{selectedUser?.isActive ? "Su sesión será revocada y dejará de ocupar un cupo activo. Su historial no se elimina." : "La activación solo será posible si la organización tiene cupo disponible."}</DialogDescription></DialogHeader><DialogFooter><Button variant="outline" onClick={() => setSelectedUser(null)}>Cancelar</Button><Button disabled={!selectedUser || changeUserState.isPending} onClick={() => selectedUser && changeUserState.mutate(selectedUser)}>{selectedUser?.isActive ? "Inactivar usuario" : "Activar usuario"}</Button></DialogFooter></DialogContent>
    </Dialog>}
  </div>;
}

function CapacityCard({ icon: Icon, title, used, allowed }: { icon: typeof UsersRound; title: string; used: number; allowed: number }) {
  const percentage = allowed === 0 ? (used > 0 ? 100 : 0) : Math.min(100, Math.round((used / allowed) * 100));
  const over = used > allowed;
  return <article className="rounded-xl border p-4"><div className="flex items-center justify-between gap-3"><span className="flex items-center gap-2 font-medium"><Icon className="h-5 w-5 text-primary" />{title}</span><Badge variant={over ? "destructive" : "secondary"}>{used} / {allowed}</Badge></div><div className="mt-4 h-2 overflow-hidden rounded-full bg-muted"><div className={over ? "h-full bg-destructive" : "h-full bg-primary"} style={{ width: `${percentage}%` }} /></div><p className="mt-2 text-xs text-muted-foreground">{over ? `Sobrecupo de ${used - allowed}` : `${Math.max(0, allowed - used)} disponibles`}</p></article>;
}
