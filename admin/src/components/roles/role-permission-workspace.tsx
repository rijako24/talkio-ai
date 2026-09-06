"use client";

import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, Copy, Eye, Save, Search } from "lucide-react";
import { toast } from "sonner";

import { navigation, type NavEntry } from "@/components/layout/sidebar-nav-config";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { PageLoading } from "@/components/ui/page-loading";
import { Textarea } from "@/components/ui/textarea";
import { rolesApi } from "@/services/api/roles";
import { useTenantContextStore } from "@/stores/tenant-context-store";
import type { Permission } from "@/types/entities";

type MenuRow = { section: string; name: string; href: string; permission: string };

const permissionScopes: Record<string, string[]> = {
  "/dashboard": ["dashboard."],
  "/dashboard/services": ["services."],
  "/dashboard/service-invoices": ["service-invoices."],
  "/dashboard/products": ["catalog."],
  "/dashboard/parties": ["parties.", "customers.", "suppliers.", "sellers.", "carriers.", "employees.", "users."],
  "/dashboard/routes": ["routes.", "route-zones."],
  "/dashboard/products/pricing": ["pricing."],
  "/dashboard/products/price-segments": ["pricing.segments."],
  "/dashboard/promotions": ["promotions."],
  "/pos": ["sales.create", "sales.discount", "sales.change-price", "sales.reprint", "sales.lines.", "sales.drafts.", "sales.void", "pos.", "work-sessions.open", "work-sessions.read", "work-sessions.close", "work-sessions.cash.", "enrolled_devices.", "fiscal.pos."],
  "/dashboard/inventory": ["inventory."],
  "/dashboard/purchasing/goods-receipts": ["purchasing.goods-receipts."],
  "/dashboard/purchasing/purchase-orders": ["purchasing.purchase-orders."],
  "/dashboard/purchasing/purchase-returns": ["purchasing.purchase-returns."],
  "/dashboard/sales-returns": ["sales.returns."],
  "/dashboard/dispatches": ["dispatches."],
  "/dashboard/deliveries": ["dispatches.delivery."],
  "/dashboard/reservations": ["reservations."],
  "/dashboard/agents": ["agents."],
  "/dashboard/channels": ["business_config."],
  "/dashboard/conversations": ["conversations."],
  "/dashboard/leads": ["leads."],
  "/dashboard/campaigns": ["campaigns."],
  "/dashboard/orders": ["orders."],
  "/dashboard/payments": ["payments."],
  "/dashboard/payables": ["payables."],
  "/dashboard/receivables": ["receivables."],
  "/dashboard/expenses": ["expenses."],
  "/dashboard/payroll": ["payroll."],
  "/dashboard/accounting": ["accounting.", "commerce.taxation."],
  "/dashboard/sales-debit-notes": ["sales.debit-notes."],
  "/dashboard/cash-differences": ["work-sessions.differences.", "work-sessions.closures."],
  "/dashboard/subscription": ["subscription."],
  "/dashboard/reports/sales": ["sales.reports."],
  "/dashboard/reports/sellers": ["sales.reports."],
  "/dashboard/reports/customers": ["sales.reports."],
  "/dashboard/reports/visits": ["sales.reports."],
  "/dashboard/reports/supplier-impact": ["sales.reports."],
  "/dashboard/tenants": ["tenants."],
  "/dashboard/businesses": ["businesses."],
  "/dashboard/roles": ["roles.", "permissions."],
  "/dashboard/audit-logs": ["audit_logs."],
  "/dashboard/settings/masters": ["masters."],
  "/dashboard/settings/fiscal": ["fiscal.configuration."],
  "/dashboard/settings": ["business_config.", "fiscal.configuration."],
};

function menuRows(): MenuRow[] {
  let section = "General";
  return navigation.flatMap((entry: NavEntry) => {
    if ("type" in entry) { section = entry.label; return []; }
    return entry.permission ? [{ section, name: entry.name, href: entry.href, permission: entry.permission }] : [];
  });
}

const actionLabels: Record<string, string> = {
  read: "Ver", create: "Crear", update: "Editar", delete: "Eliminar", confirm: "Confirmar",
  export: "Exportar", cancel: "Cancelar", send: "Enviar", manage: "Administrar",
  assign_role: "Asignar roles", remove_role: "Retirar roles", assign_permissions: "Asignar permisos",
  confirm_manual: "Confirmar manualmente", reset: "Restablecer credenciales",
};

function permissionViewLabel(resource: string) {
  if (resource.startsWith("users.") || resource.startsWith("security.users.")) return "Terceros · Usuarios";
  if (resource.startsWith("tenants.") || resource.startsWith("platform.")) return "Empresas";
  if (resource.startsWith("accounting.") || resource.startsWith("commerce.taxation.")) return "Contabilidad";
  if (resource.startsWith("fiscal.")) return "Configuración · DIAN";
  if (resource.startsWith("masters.")) return "Configuración · Maestros";
  return "Administración";
}

export function RolePermissionWorkspace({ roleId, cloneFromId, embedded = false, readOnly = false, onSaved, onClose }: { roleId?: string; cloneFromId?: string; embedded?: boolean; readOnly?: boolean; onSaved?: (roleId: string) => void; onClose?: () => void }) {
  const queryClient = useQueryClient();
  const tenantId = useTenantContextStore((state) => state.selectedTenantId);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState("");
  const [hydrated, setHydrated] = useState(false);
  const [nameError, setNameError] = useState<string>();
  const sourceId = roleId ?? cloneFromId;
  const roleQuery = useQuery({ queryKey: ["roles", sourceId], queryFn: () => rolesApi.getById(sourceId!), enabled: Boolean(sourceId) });
  const permissionsQuery = useQuery({ queryKey: ["permissions", "catalog"], queryFn: rolesApi.getPermissionCatalog });
  const assignedQuery = useQuery({ queryKey: ["roles", sourceId, "permissions"], queryFn: () => rolesApi.getAssignedPermissions(sourceId!), enabled: Boolean(sourceId) });

  useEffect(() => {
    if (hydrated || permissionsQuery.isLoading || (sourceId && (roleQuery.isLoading || assignedQuery.isLoading))) return;
    const role = roleQuery.data;
    setName(cloneFromId && role ? `${role.name} - copia` : role?.name ?? "");
    setDescription(role?.description ?? "");
    setSelected(new Set((assignedQuery.data ?? []).map((permission) => permission.permissionId)));
    setHydrated(true);
  }, [assignedQuery.data, assignedQuery.isLoading, cloneFromId, hydrated, permissionsQuery.isLoading, roleQuery.data, roleQuery.isLoading, sourceId]);

  const permissions = permissionsQuery.data ?? [];
  const rows = useMemo(() => menuRows(), []);
  const matching = (row: MenuRow) => {
    const scopes = permissionScopes[row.href] ?? [row.permission.replace(/\.read$/, ".")];
    return permissions
      .filter((permission) => scopes.some((scope) => permission.resource.startsWith(scope)))
      .filter((permission) => row.href === "/dashboard/products/price-segments" || !permission.resource.startsWith("pricing.segments."))
      .filter((permission) => row.href !== "/dashboard/channels" || permission.resource === "agents.read");
  };
  const filteredRows = rows.filter((row) => !search.trim() || `${row.section} ${row.name} ${row.href}`.toLowerCase().includes(search.toLowerCase()));
  const additional = permissions.filter((permission) => !rows.some((row) => matching(row).some((item) => item.permissionId === permission.permissionId)));
  const isSystemRole = Boolean(roleId && roleQuery.data?.isSystemRole);
  const locked = readOnly || isSystemRole;

  const save = useMutation({
    mutationFn: async () => {
      if (!name.trim()) { setNameError("Este campo es requerido"); throw new Error("Revisa el campo resaltado."); }
      setNameError(undefined);
      let targetId = roleId;
      if (targetId) await rolesApi.update(targetId, { name: name.trim(), description: description.trim() || null });
      else {
        const created = await rolesApi.create({ tenantId, name: name.trim(), description: description.trim() || null });
        targetId = created.roleId;
      }
      await rolesApi.replacePermissions(targetId!, [...selected]);
      return targetId!;
    },
    onSuccess: async (id) => {
      await queryClient.invalidateQueries({ queryKey: ["roles"] });
      toast.success("Rol y permisos guardados.");
      onSaved?.(id);
    },
    onError: (error: { message?: string }) => toast.error(error.message ?? "No fue posible guardar el rol."),
  });

  const toggle = (permission: Permission, checked: boolean) => setSelected((current) => {
    const next = new Set(current);
    if (checked) next.add(permission.permissionId); else next.delete(permission.permissionId);
    return next;
  });
  const toggleView = (row: MenuRow, checked: boolean) => {
    const permission = permissions.find((item) => item.resource === row.permission);
    if (permission) toggle(permission, checked);
  };

  if (permissionsQuery.isLoading || (sourceId && roleQuery.isLoading)) return <PageLoading cards={3} />;

  return <div className="space-y-6">
    <header className="flex flex-wrap items-center gap-4">
      <div className="min-w-0 flex-1"><h1 className="text-2xl font-semibold tracking-tight">{roleId ? "Configurar rol" : cloneFromId ? "Duplicar rol" : "Nuevo rol"}</h1><p className="text-muted-foreground">Define primero qué aparece en el menú y luego qué acciones permite cada vista.</p></div>
      {roleId && !cloneFromId && !embedded && <Button variant="outline" disabled><Copy className="mr-2 h-4 w-4" />Duplicar desde la lista</Button>}
      {onClose && <Button variant="outline" onClick={onClose}>Cerrar</Button>}
      {!readOnly && <Button disabled={save.isPending || isSystemRole} onClick={() => save.mutate()}><Save className="mr-2 h-4 w-4" />Guardar rol</Button>}
    </header>
    {isSystemRole && <div className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">Este rol protege funciones esenciales del sistema y es de solo lectura. Puedes duplicarlo para crear una variante.</div>}
    <Card><CardHeader><CardTitle>Identidad del rol</CardTitle></CardHeader><CardContent className="grid gap-4 md:grid-cols-2"><div className="space-y-2"><Label htmlFor="role-name">Nombre <span className="text-destructive">*</span></Label><Input id="role-name" aria-invalid={Boolean(nameError)} className={nameError ? "border-destructive ring-1 ring-destructive/20" : ""} value={name} onChange={(event) => { setName(event.target.value); if (event.target.value.trim()) setNameError(undefined); }} disabled={locked} placeholder="Ej. Coordinador de inventario" />{nameError && <p className="text-sm text-destructive">{nameError}</p>}</div><div className="space-y-2"><Label htmlFor="role-description">Descripción</Label><Textarea id="role-description" value={description} onChange={(event) => setDescription(event.target.value)} disabled={locked} rows={2} /></div></CardContent></Card>
    <div className="grid gap-3 sm:grid-cols-3"><Summary label="Vistas visibles" value={String(rows.filter((row) => permissions.some((permission) => permission.resource === row.permission && selected.has(permission.permissionId))).length)} /><Summary label="Acciones asignadas" value={String(selected.size)} /><Summary label="Permisos disponibles" value={String(permissions.length)} /></div>
    <Card><CardHeader><div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between"><div><CardTitle>Menú y acciones por vista</CardTitle><p className="mt-1 text-sm text-muted-foreground">Desmarcar “Ver” oculta la vista. Las acciones controlan lo que puede hacer dentro.</p></div><div className="relative"><Search className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><Input className="w-full pl-9 md:w-72" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Buscar vista o sección" /></div></div></CardHeader><CardContent className="space-y-3">
      {filteredRows.map((row) => {
        const viewPermission = permissions.find((permission) => permission.resource === row.permission);
        const actions = matching(row);
        const visible = Boolean(viewPermission && selected.has(viewPermission.permissionId));
        return <details key={row.href} className="group rounded-xl border bg-card" open={search.trim().length > 0}>
          <summary className="flex cursor-pointer list-none items-center gap-3 p-4"><Checkbox checked={visible} disabled={!viewPermission || isSystemRole} onCheckedChange={(checked) => toggleView(row, checked === true)} onClick={(event) => event.stopPropagation()} /><div className="min-w-0 flex-1"><div className="flex flex-wrap items-center gap-2"><span className="font-medium">{row.name}</span><Badge variant="outline">{row.section}</Badge></div><p className="truncate text-xs text-muted-foreground">{row.href}</p></div><span className="text-xs text-muted-foreground">{actions.filter((item) => selected.has(item.permissionId)).length}/{actions.length} acciones</span><ChevronDown className="h-4 w-4 transition group-open:rotate-180" /></summary>
          <div className="grid gap-2 border-t p-4 sm:grid-cols-2 lg:grid-cols-3">{actions.map((permission) => { const translatedAction=actionLabels[permission.resource.split(".").at(-1)!]; return <label key={permission.permissionId} className="flex cursor-pointer items-start gap-3 rounded-lg border p-3 hover:bg-muted/40"><Checkbox checked={selected.has(permission.permissionId)} disabled={locked} onCheckedChange={(checked) => toggle(permission, checked === true)} /><span><span className="block text-sm font-medium">{translatedAction ?? permission.description ?? "Acción disponible"}</span>{translatedAction&&permission.description&&<span className="block text-xs text-muted-foreground">{permission.description}</span>}</span></label>; })}</div>
        </details>;
      })}
    </CardContent></Card>
    {additional.length > 0 && <Card><CardHeader><CardTitle>Otras acciones por vista</CardTitle><p className="text-sm text-muted-foreground">Cada acción se muestra con la vista administrativa donde se utiliza.</p></CardHeader><CardContent className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">{additional.map((permission) => <label key={permission.permissionId} className="flex gap-3 rounded-lg border p-3"><Checkbox checked={selected.has(permission.permissionId)} disabled={locked} onCheckedChange={(checked) => toggle(permission, checked === true)} /><span><span className="block text-sm font-medium">{permission.description ?? "Acción disponible"}</span><span className="text-xs text-muted-foreground">Vista: {permissionViewLabel(permission.resource)}</span></span></label>)}</CardContent></Card>}
  </div>;
}

function Summary({ label, value }: { label: string; value: string }) {
  return <Card><CardContent className="flex items-center gap-3 pt-6"><div className="rounded-lg bg-emerald-50 p-2 text-emerald-700"><Eye className="h-4 w-4" /></div><div><p className="text-sm text-muted-foreground">{label}</p><p className="text-xl font-semibold">{value}</p></div></CardContent></Card>;
}
