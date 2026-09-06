"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { CalendarDays, CircleDollarSign, KeyRound, ReceiptText, Scissors, X } from "lucide-react";
import { ScheduleExceptionsEditor } from "@/components/settings/schedule-exceptions-editor";
import { WorkingHoursEditor } from "@/components/settings/working-hours-editor";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { useRoles } from "@/hooks/use-roles";
import { useServices } from "@/hooks/use-services";
import { useCities } from "@/hooks/use-parties";
import { useReferenceOptions } from "@/hooks/use-reference-options";
import { employeesApi, usersApi } from "@/services/api";
import type { PartySiteDetail, UserRoleDetail } from "@/services/api/parties";
import { configuredPasswordMask, effectiveUserRoleAssignments } from "@/lib/party-user-role-selection";
import { counterpartyWithholdingRuleIsCandidate, isIncomeTaxSelfWithholdingSupplier } from "@/lib/party-tax-profile";
import { taxationApi, type WithholdingRule } from "@/services/api/taxation";
import { receivablesApi } from "@/services/api/receivables";
import { payrollApi } from "@/services/api/payroll";
import { posApprovalClient } from "@/services/pos/pos-approval-client";
import { formatDecimalInput, parseDecimalInput } from "@/lib/formatted-decimal-input";
import { useAuthStore } from "@/stores/auth-store";
import { useBusinessContextStore } from "@/stores/business-context-store";
import type { WorkingHour } from "@/types/entities";

const defaultHours: WorkingHour[] = [{ dayOfWeek: 1, openTime: "08:00", closeTime: "17:00", isActive: true }];

type RegisterSave = (key: string, handler: () => Promise<void>) => () => void;

const creditMoney = new Intl.NumberFormat("es-CO", { style: "currency", currency: "COP", maximumFractionDigits: 0 });

export function PartyCustomerCreditRolePanel({ customerId, editing, registerSave }: { customerId: string; editing: boolean; registerSave: RegisterSave }) {
  const businessId = useBusinessContextStore((state) => state.selectedBusinessId);
  const permissions = useAuthStore((state) => new Set(state.user?.permissions ?? []));
  const canRead = permissions.has("receivables.read");
  const canManage = permissions.has("receivables.credit.manage");
  const queryClient = useQueryClient();
  const profile = useQuery({ queryKey: ["customer-credit", businessId, customerId], queryFn: () => receivablesApi.getCreditProfile(customerId), enabled: Boolean(businessId && customerId && canRead), retry: false });
  const [enabled, setEnabled] = useState(false);
  const [limit, setLimit] = useState("");
  const [dueDays, setDueDays] = useState("30");
  useEffect(() => {
    if (!profile.data) return;
    setEnabled(profile.data.isCreditEnabled);
    setLimit(profile.data.creditLimit == null ? "" : formatDecimalInput(String(profile.data.creditLimit), 0));
    setDueDays(String(profile.data.defaultDueDays));
  }, [profile.data]);
  const save = async () => {
    if (!businessId || !canManage) return;
    const days = Number(dueDays);
    const parsedLimit = limit.trim() ? parseDecimalInput(limit) : null;
    if (!Number.isInteger(days) || days < 0 || days > 3650 || (parsedLimit != null && (!Number.isFinite(parsedLimit) || parsedLimit < 0)))
      throw new Error("Revisa el cupo y el plazo de crédito.");
    await receivablesApi.updateCreditProfile(customerId, { businessId, creditLimit: parsedLimit, defaultDueDays: days, isCreditEnabled: enabled });
    await queryClient.invalidateQueries({ queryKey: ["customer-credit", businessId, customerId] });
  };
  useEffect(() => registerSave(`customer-credit-${customerId}`, save), [registerSave, customerId, businessId, canManage, enabled, limit, dueDays]);

  if (!canRead) return <PanelError text="No tienes permiso para consultar la configuración de cartera de este cliente." />;
  if (profile.isLoading) return <PanelLoading />;
  if (!profile.data) return <PanelError text="No fue posible cargar la configuración de crédito." />;
  return <div className="space-y-4">
    <PanelHeader icon={CircleDollarSign} title="Crédito y cartera" description="Define si este cliente puede dejar saldo pendiente al facturar.">
      <div className="flex items-center gap-3"><span className="text-sm">Permite ventas a crédito</span><Switch checked={enabled} onCheckedChange={setEnabled} disabled={!editing || !canManage}/></div>
    </PanelHeader>
    <section className="grid gap-4 rounded-2xl border bg-muted/10 p-5 md:grid-cols-2">
      {editing&&canManage?<CustomerCreditTermsFields enabled={enabled} limit={limit} dueDays={dueDays} onLimitChange={setLimit} onDueDaysChange={setDueDays}/>:<><CreditReadValue label="Cupo de crédito" value={profile.data.creditLimit == null ? "Sin límite configurado" : creditMoney.format(profile.data.creditLimit)} help="Límite máximo de saldo pendiente."/><CreditReadValue label="Vencimiento predeterminado (días)" value={`${profile.data.defaultDueDays} días`} help="Se suma a la fecha de la venta para calcular el vencimiento."/></>}
      <div><Label>Saldo pendiente</Label><p className="mt-2 text-lg font-semibold">{creditMoney.format(profile.data.outstandingAmount)}</p></div>
      <div><Label>Cupo disponible</Label><p className="mt-2 text-lg font-semibold">{profile.data.availableCredit == null ? "Sin límite" : creditMoney.format(profile.data.availableCredit)}</p></div>
      {!canManage&&editing&&<p className="md:col-span-2 text-sm text-amber-700">Puedes editar la ficha, pero no las condiciones de cartera porque falta el permiso correspondiente.</p>}
    </section>
  </div>;
}

export function CustomerCreditTermsFields({ enabled, limit, dueDays, onLimitChange, onDueDaysChange }: { enabled: boolean; limit: string; dueDays: string; onLimitChange: (value: string) => void; onDueDaysChange: (value: string) => void }) {
  return <>
    <div className="space-y-2"><Label>Cupo de crédito (COP)</Label><div className="relative"><span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 font-semibold text-muted-foreground">$</span><Input className="pl-8 text-right tabular-nums" inputMode="numeric" value={limit} disabled={!enabled} onChange={(event)=>onLimitChange(formatDecimalInput(event.target.value,0))} placeholder="Sin límite"/></div><p className="text-xs text-muted-foreground">Vacío significa que no hay un límite monetario.</p></div>
    <div className="space-y-2"><Label>Vencimiento predeterminado (días)</Label><div className="relative"><Input className="pr-14 text-right tabular-nums" inputMode="numeric" value={dueDays} disabled={!enabled} onChange={(event)=>onDueDaysChange(event.target.value.replace(/\D/g,"").slice(0,4))}/><span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-sm text-muted-foreground">días</span></div><p className="text-xs text-muted-foreground">Se suma a la fecha de venta para calcular cuándo vence la cuenta por cobrar.</p></div>
    {!enabled&&<p className="rounded-xl border border-dashed bg-background p-3 text-sm text-muted-foreground md:col-span-2">Activa “Permite ventas a crédito” para configurar el cupo y el vencimiento.</p>}
  </>;
}

function CreditReadValue({label,value,help}:{label:string;value:string;help:string}) { return <div className="space-y-2"><Label>{label}</Label><p className="rounded-xl border bg-background p-3 font-medium">{value}</p><p className="text-xs text-muted-foreground">{help}</p></div>; }

export function PartyCustomerTaxRolePanel({ customerId, editing, primarySite, registerSave }: { customerId: string; editing: boolean; primarySite: PartySiteDetail | null; registerSave: RegisterSave }) {
  return <PartyCounterpartyTaxRolePanel counterpartyId={customerId} role="customer" editing={editing} primarySite={primarySite} registerSave={registerSave}/>;
}

export function PartySupplierTaxRolePanel({ supplierId, editing, primarySite, registerSave }: { supplierId: string; editing: boolean; primarySite: PartySiteDetail | null; registerSave: RegisterSave }) {
  return <PartyCounterpartyTaxRolePanel counterpartyId={supplierId} role="supplier" editing={editing} primarySite={primarySite} registerSave={registerSave}/>;
}

function PartyCounterpartyTaxRolePanel({ counterpartyId, role, editing, primarySite, registerSave }: { counterpartyId: string; role: "customer" | "supplier"; editing: boolean; primarySite: PartySiteDetail | null; registerSave: RegisterSave }) {
  const businessId = useBusinessContextStore((state) => state.selectedBusinessId);
  const queryClient = useQueryClient();
  const profile = useQuery({ queryKey: ["withholding-profile", businessId, counterpartyId], queryFn: () => taxationApi.getProfile(counterpartyId), enabled: Boolean(businessId && counterpartyId), retry: false });
  const rules = useQuery({ queryKey: ["withholding-rules", businessId, `${role}-profile-options`], queryFn: () => taxationApi.listRules(false), enabled: Boolean(businessId) });
  const responsibilityOptions = useReferenceOptions("tax-responsibility", Boolean(businessId));
  const cities = useCities(primarySite?.administrativeDivisionId ?? "");
  const [appliesWithholding, setAppliesWithholding] = useState(false);
  const [responsibilities, setResponsibilities] = useState<Set<string>>(new Set());
  const [responsibilityToAdd, setResponsibilityToAdd] = useState("");

  useEffect(() => {
    setAppliesWithholding(profile.data?.appliesWithholding ?? false);
    setResponsibilities(new Set(profile.data?.responsibilities ?? []));
  }, [profile.data]);

  const save = async () => {
    if (!businessId) return;
    const cityCode = cities.data?.find((city) => city.cityId === primarySite?.cityId)?.code ?? profile.data?.jurisdictionCode ?? null;
    await taxationApi.saveProfile({ businessId, counterpartyId, appliesWithholding, responsibilities: [...responsibilities], jurisdictionCode: cityCode });
    await queryClient.invalidateQueries({ queryKey: ["withholding-profile", businessId, counterpartyId] });
  };
  useEffect(() => registerSave(`${role}-tax-${counterpartyId}`, save), [registerSave, role, counterpartyId, businessId, appliesWithholding, responsibilities, cities.data, primarySite?.cityId, profile.data?.jurisdictionCode]);

  const responsibilityLabels = new Map((responsibilityOptions.data ?? [])
    .map((option) => [option.code, option.label] as const));
  const catalog = (responsibilityOptions.data ?? []).map((option) => option.code);
  const available = catalog.filter((code) => !responsibilities.has(code));
  const city = cities.data?.find((item) => item.cityId === primarySite?.cityId);

  return <div className="space-y-4">
    <PanelHeader icon={ReceiptText} title="Retenciones y perfil tributario" description={`Registra todas las responsabilidades vigentes del RUT de este ${role === "customer" ? "cliente" : "proveedor"}; no representan tarifas.`}><div className="flex items-center gap-3"><span className="text-sm">Aplicar retenciones</span><Switch checked={appliesWithholding} onCheckedChange={setAppliesWithholding} disabled={!editing}/></div></PanelHeader>
    {profile.isLoading ? <PanelLoading /> : <section className="space-y-4 rounded-2xl border p-5">
      <div className="grid gap-4 md:grid-cols-2">
        <div className={editing ? "space-y-2" : "grid content-start grid-rows-[auto_3rem_auto] gap-2"}><Label>Responsabilidades tributarias</Label>{editing&&<Select value={responsibilityToAdd} disabled={responsibilityOptions.isLoading||responsibilityOptions.isError||available.length===0} onValueChange={(value) => { setResponsibilityToAdd(""); setResponsibilities((current) => new Set(current).add(value)); }}><SelectTrigger><SelectValue placeholder={responsibilityOptions.isLoading?"Cargando catálogo...":responsibilityOptions.isError?"No fue posible cargar el catálogo":catalog.length===0?"No hay responsabilidades configuradas":"Agregar responsabilidad"}/></SelectTrigger><SelectContent>{available.map((code) => <SelectItem key={code} value={code}>{code} · {responsibilityLabels.get(code) ?? "Responsabilidad configurada"}</SelectItem>)}</SelectContent></Select>}<div className="flex min-h-12 flex-wrap items-center gap-2 rounded-xl border bg-muted/10 p-3">{[...responsibilities].map((code) => <Badge key={code} variant="secondary" className="gap-1">{code} · {responsibilityLabels.get(code) ?? "Responsabilidad configurada"}{editing&&<button type="button" aria-label={`Quitar ${code}`} onClick={() => setResponsibilities((current) => { const next = new Set(current); next.delete(code); return next; })}><X className="h-3 w-3"/></button>}</Badge>)}{responsibilities.size===0&&<span className="text-sm text-muted-foreground">Sin responsabilidades seleccionadas</span>}</div><p className="text-xs text-muted-foreground">Un tercero puede tener varias calidades vigentes en su RUT. Las tarifas pertenecen a las reglas, no a estas responsabilidades. <Link className="font-medium text-primary underline underline-offset-2" href="/dashboard/accounting?section=withholdings">Abrir Retenciones</Link></p></div>
        <div className="grid content-start grid-rows-[auto_3rem_auto] gap-2"><Label>Ciudad o jurisdicción tributaria</Label><Input className="h-12" value={city?`${city.name} (${city.code})`:cities.isError?"No fue posible cargar la ciudad":primarySite?"Ciudad de la sede no disponible":"Sin sede principal"} readOnly/><p className="text-xs text-muted-foreground">Se sincroniza automáticamente con la ciudad de la sede principal.</p></div>
      </div>
      <CounterpartyWithholdingRules rules={rules.data ?? []} loading={rules.isLoading} error={rules.isError} role={role} responsibilities={responsibilities} jurisdictionCode={city?.code ?? profile.data?.jurisdictionCode ?? null}/>
    </section>}
  </div>;
}

export function CounterpartyWithholdingRules({ rules, loading, error, role, responsibilities = new Set(), jurisdictionCode = null }: { rules: WithholdingRule[]; loading: boolean; error: boolean; role: "customer" | "supplier"; responsibilities?: ReadonlySet<string>; jurisdictionCode?: string | null }) {
  const applicable = rules.filter((rule) => counterpartyWithholdingRuleIsCandidate(
    rule, role, responsibilities, jurisdictionCode,
  ));
  const selfWithholdingSupplier = isIncomeTaxSelfWithholdingSupplier(role, responsibilities);
  return <div className="space-y-2 border-t pt-4">
    <Label>Reglas tributarias aplicables</Label>
    {selfWithholdingSupplier&&<p className="rounded-xl border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-900">ReteFuente de renta no aplicable: el proveedor está registrado como autorretenedor. Otras retenciones se evalúan normalmente.</p>}
    {loading ? <p className="text-sm text-muted-foreground">Cargando reglas…</p>
      : error ? <p className="text-sm text-destructive">No fue posible cargar las reglas de retención.</p>
      : applicable.length ? <div className="flex flex-wrap gap-2">{applicable.map((rule) => <Badge key={rule.ruleId} variant="outline">{rule.name} · {rule.rate}%</Badge>)}</div>
      : <p className="text-sm text-muted-foreground">Ninguna regla activa coincide con la operación, responsabilidades y jurisdicción actuales.</p>}
    <p className="text-xs text-muted-foreground">Esta lista es informativa. El motor confirma vigencia, concepto y base mínima cuando contabiliza cada documento.</p>
  </div>;
}

export function PartyEmployeeRolePanel({ partyId, employeeId, editing, registerSave }: { partyId: string; employeeId: string; editing: boolean; registerSave: RegisterSave }) {
  const businessId = useBusinessContextStore((state) => state.selectedBusinessId);
  const canReadPayroll = useAuthStore((state) => state.user?.permissions.includes("payroll.read") ?? false);
  const employeeQuery = useQuery({ queryKey: ["employees", employeeId], queryFn: () => employeesApi.getById(employeeId) });
  const hoursQuery = useQuery({ queryKey: ["employees", employeeId, "working-hours"], queryFn: () => employeesApi.getWorkingHours(employeeId) });
  const services = useServices({ page: 1, pageSize: 500 });
  const payroll = useQuery({ queryKey: ["payroll-options", businessId, partyId], queryFn: payrollApi.options, enabled: Boolean(businessId && partyId && canReadPayroll), retry: false });
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [serviceToAdd, setServiceToAdd] = useState("");
  const [active, setActive] = useState(true);
  const [customSchedule, setCustomSchedule] = useState(false);
  const [workingHours, setWorkingHours] = useState<WorkingHour[]>(defaultHours);

  useEffect(() => {
    if (!employeeQuery.data) return;
    setSelectedIds(new Set(employeeQuery.data.serviceIds ?? []));
    setActive(employeeQuery.data.isActive);
  }, [employeeQuery.data]);
  useEffect(() => {
    if (!hoursQuery.data) return;
    setCustomSchedule(!hoursQuery.data.usesBusinessFallback);
    setWorkingHours(hoursQuery.data.workingHours.length ? hoursQuery.data.workingHours : defaultHours);
  }, [hoursQuery.data]);

  const save = async () => {
    const employee = employeeQuery.data;
    if (!employee) return;
    await employeesApi.update(employeeId, { name: employee.name, isActive: active, serviceIds: [...selectedIds] });
    await employeesApi.updateWorkingHours(employeeId, customSchedule ? workingHours : []);
    await Promise.all([employeeQuery.refetch(), hoursQuery.refetch()]);
  };
  useEffect(() => registerSave(`employee-${employeeId}`, save), [registerSave, employeeId, employeeQuery.data, active, selectedIds, customSchedule, workingHours]);

  if (employeeQuery.isLoading || hoursQuery.isLoading) return <PanelLoading />;
  if (!employeeQuery.data) return <PanelError text="No fue posible cargar la configuración del empleado." />;
  const available = (services.data?.items ?? []).filter((item) => item.isActive && !selectedIds.has(item.serviceId));
  const employment = payroll.data?.employments.find((item) => item.partyId === partyId);

  return <div className="space-y-5">
    <PanelHeader icon={Scissors} title="Configuración del empleado" description="Servicios, disponibilidad y estado en el mismo tercero.">
      <div className="flex items-center gap-3"><span className="text-sm text-muted-foreground">Activo</span><Switch checked={active} onCheckedChange={setActive} disabled={!editing}/></div>
    </PanelHeader>
    <section className="space-y-3 rounded-2xl border p-5">
      <div><h3 className="font-semibold">Servicios asignados</h3><p className="text-sm text-muted-foreground">Agrega los servicios que este empleado puede atender.</p></div>
      <Select value={serviceToAdd} onValueChange={(value) => { setServiceToAdd(""); setSelectedIds((current) => new Set(current).add(value)); }} disabled={!editing}>
        <SelectTrigger><SelectValue placeholder={services.isLoading ? "Cargando servicios..." : "Agregar servicio"} /></SelectTrigger>
        <SelectContent>{available.map((service) => <SelectItem key={service.serviceId} value={service.serviceId}>{service.serviceName}</SelectItem>)}{available.length === 0 && <SelectItem value="_none" disabled>Sin datos</SelectItem>}</SelectContent>
      </Select>
      <div className="grid gap-3 sm:grid-cols-2">{[...selectedIds].map((serviceId) => { const service = services.data?.items.find((item) => item.serviceId === serviceId); return <div key={serviceId} className="flex items-center justify-between rounded-xl border bg-card p-4"><div><p className="font-medium">{service?.serviceName ?? "Servicio"}</p><p className="text-xs text-muted-foreground">Disponible para asignaciones</p></div>{editing&&<Button type="button" variant="ghost" size="icon" onClick={() => setSelectedIds((current) => { const next = new Set(current); next.delete(serviceId); return next; })}><X className="h-4 w-4" /></Button>}</div>; })}{selectedIds.size === 0 && <EmptyState text="Sin datos. Agrega los servicios que atenderá esta persona." />}</div>
    </section>
    <section className="space-y-4 rounded-2xl border p-5">
      <div className="flex items-center justify-between gap-4"><div><h3 className="font-semibold">Calendario activo</h3><p className="text-sm text-muted-foreground">Desactivado reutiliza automáticamente el horario del negocio.</p></div><Switch checked={customSchedule} onCheckedChange={setCustomSchedule} disabled={!editing}/></div>
      {customSchedule ? editing?<WorkingHoursEditor value={workingHours} onChange={setWorkingHours}/>:<div className="rounded-xl border bg-muted/20 p-4 text-sm text-muted-foreground">Horario personalizado configurado.</div> : <div className="flex items-center gap-3 rounded-xl bg-muted/40 p-4 text-sm text-muted-foreground"><CalendarDays className="h-5 w-5 text-primary" />Usará el calendario activo configurado para el negocio.</div>}
    </section>
    {editing&&<section className="rounded-2xl border p-5"><h3 className="font-semibold">Excepciones del calendario</h3><p className="mb-4 text-sm text-muted-foreground">Cierres o cambios puntuales para fechas específicas.</p><ScheduleExceptionsEditor employeeId={employeeId}/></section>}
    <section className="rounded-2xl border p-5"><div className="flex flex-wrap items-start justify-between gap-4"><div><h3 className="font-semibold">Relación laboral y nómina</h3><p className="text-sm text-muted-foreground">Contrato, salario, descuentos y pagos se administran en Nómina sin duplicarlos en el empleado operativo.</p></div>{canReadPayroll&&<Button asChild variant="outline"><Link href={`/dashboard/payroll?section=employments&partyId=${partyId}`}>{employment?"Abrir contrato laboral":"Crear contrato laboral"}</Link></Button>}</div>{canReadPayroll?(payroll.isLoading?<p className="mt-4 text-sm text-muted-foreground">Consultando relación laboral…</p>:employment?<div className="mt-4 grid gap-3 sm:grid-cols-3"><CreditReadValue label="Contrato" value={employment.contractNumber} help={`${employment.startDate} — ${employment.endDate??"vigente"}`}/><CreditReadValue label="Salario mensual" value={creditMoney.format(employment.monthlySalary)} help="Fuente canónica: Nómina"/><CreditReadValue label="Estado laboral" value={employment.isActive?"Activo":"Inactivo"} help="Independiente del horario de agenda"/></div>:<p className="mt-4 rounded-xl border border-dashed p-4 text-sm text-muted-foreground">Esta persona aún no tiene una relación laboral configurada para la empresa seleccionada.</p>):<p className="mt-4 text-sm text-muted-foreground">No tienes permiso para consultar la información de nómina.</p>}</section>
  </div>;
}

export function PartyUserRolePanel({ user, editing, registerSave }: { user: UserRoleDetail; editing: boolean; registerSave: RegisterSave }) {
  const userId = user.userId;
  const businessId = useBusinessContextStore((state) => state.selectedBusinessId);
  const roles = useRoles({ page: 1, pageSize: 500 });
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [roleToAdd, setRoleToAdd] = useState("");
  const [active, setActive] = useState(true);
  const [newPassword, setNewPassword] = useState("");
  const [roleError, setRoleError] = useState("");
  const [passwordError, setPasswordError] = useState("");
  const canManageUsers = useAuthStore((state) =>
    state.user?.permissions.includes("users.update") ?? false);
  const canManageApprovalCredential = useAuthStore((state) =>
    state.user?.permissions.includes("pos.approvals.manage_credential") ?? false);
  const approvalCredential = useQuery({
    queryKey: ["supervisor-credential", userId],
    queryFn: () => posApprovalClient.userCredentialStatus(userId),
    enabled: canManageApprovalCredential,
    retry: false,
  });
  const [approvalSecret, setApprovalSecret] = useState("");
  const [approvalConfirmation, setApprovalConfirmation] = useState("");
  const [approvalValidity, setApprovalValidity] = useState<"once" | "8" | "168" | "always">("always");
  const [revokeApprovalCredential, setRevokeApprovalCredential] = useState(false);
  const scopedAssignments = useMemo(
    () => effectiveUserRoleAssignments(user.roles, businessId),
    [user.roles, businessId],
  );

  useEffect(() => { setActive(user.isActive); }, [user.isActive]);
  useEffect(() => { setSelectedIds(new Set(scopedAssignments.map((item) => item.roleId))); }, [scopedAssignments]);

  const save = async () => {
    if (!businessId) return;
    const nextRoleError = selectedIds.size === 0 ? "Este campo es requerido" : "";
    const nextPasswordError = newPassword && newPassword.length < 10 ? "Debe tener al menos 10 caracteres" : "";
    setRoleError(nextRoleError); setPasswordError(nextPasswordError);
    if (nextRoleError || nextPasswordError) throw new Error(nextRoleError || nextPasswordError);
    const currentIds = new Set(scopedAssignments.map((item) => item.roleId));
    await Promise.all([
      ...[...selectedIds].filter((roleId) => !currentIds.has(roleId)).map((roleId) => usersApi.assignRole(userId, { roleId, businessId })),
      ...scopedAssignments.filter((item) => !selectedIds.has(item.roleId)).map((item) => usersApi.removeRole(userId, item.roleId, item.businessId)),
    ]);
    if (active !== user.isActive) await (active ? usersApi.activate(userId) : usersApi.deactivate(userId));
    if (canManageUsers && newPassword) await usersApi.resetPassword(userId, newPassword);
    if (canManageApprovalCredential && revokeApprovalCredential) {
      await posApprovalClient.revokeUserCredential(userId);
      setRevokeApprovalCredential(false);
      await approvalCredential.refetch();
    } else if (canManageApprovalCredential && approvalSecret) {
      if (approvalSecret.length < 6 || approvalSecret !== approvalConfirmation)
        throw new Error("La clave de autorización debe tener mínimo 6 caracteres y coincidir.");
      await posApprovalClient.configureUserCredential(
        userId,
        approvalSecret,
        approvalValidity === "once" || approvalValidity === "always" ? null : Number(approvalValidity) as 8 | 168,
        approvalValidity === "once",
      );
      setApprovalSecret("");
      setApprovalConfirmation("");
      await approvalCredential.refetch();
    }
    setNewPassword("");
  };
  useEffect(() => registerSave(`user-${userId}`, save), [registerSave, userId, businessId, user, scopedAssignments, selectedIds, active, newPassword, canManageUsers, canManageApprovalCredential, revokeApprovalCredential, approvalSecret, approvalConfirmation, approvalValidity]);

  const available = (roles.data?.items ?? []).filter((item) => item.isActive && !selectedIds.has(item.roleId));
  return <div className="space-y-5">
    <PanelHeader icon={KeyRound} title="Acceso al sistema" description="Inicio de sesión, roles y autorizaciones de caja.">
      <div className="flex items-center gap-3"><span className="text-sm text-slate-300">Usuario habilitado</span><Switch checked={active} onCheckedChange={setActive} disabled={!editing}/></div>
    </PanelHeader>
    <section className={`space-y-3 rounded-2xl border p-5 ${roleError ? "border-destructive" : ""}`}><div><h3 className="font-semibold">Roles en este negocio</h3><p className="text-sm text-muted-foreground">Definen los menús visibles y las acciones habilitadas en cada vista.</p></div>{editing&&<Select value={roleToAdd} onValueChange={(value) => { setRoleToAdd(""); setRoleError(""); setSelectedIds((current) => new Set(current).add(value)); }}><SelectTrigger aria-invalid={Boolean(roleError)} className={roleError ? "border-destructive" : ""}><SelectValue placeholder={roles.isLoading ? "Cargando roles..." : "Agregar rol"} /></SelectTrigger><SelectContent>{available.map((role) => <SelectItem key={role.roleId} value={role.roleId}>{role.name}</SelectItem>)}{available.length === 0 && <SelectItem value="_none" disabled>Sin datos</SelectItem>}</SelectContent></Select>}{roleError && <p className="text-sm text-destructive">{roleError}</p>}<div className="grid gap-3 sm:grid-cols-2">{[...selectedIds].map((roleId) => { const role = roles.data?.items.find((item) => item.roleId === roleId); const assigned = scopedAssignments.find((item) => item.roleId === roleId); return <div key={roleId} className="flex items-center justify-between rounded-xl border bg-card p-4"><div><p className="font-medium">{role?.name ?? assigned?.roleName ?? "Rol"}</p><p className="text-xs text-muted-foreground">{role?.description ?? "Permisos asignados"}</p></div>{editing&&<Button type="button" variant="ghost" size="icon" onClick={() => setSelectedIds((current) => { const next = new Set(current); next.delete(roleId); return next; })}><X className="h-4 w-4" /></Button>}</div>; })}{selectedIds.size === 0 && <EmptyState text="Sin roles asignados en este negocio." />}</div></section>
    {editing&&canManageUsers&&<section className={`space-y-3 rounded-2xl border p-5 ${passwordError ? "border-destructive" : ""}`}><div><Label htmlFor={`reset-${userId}`}>Restablecer contraseña de acceso y modo sin conexión POS</Label><p className="text-sm text-muted-foreground">Por seguridad la contraseña actual nunca se puede consultar. Los puntos indican que ya existe una; escribe una nueva solo para reemplazarla.</p></div><Input id={`reset-${userId}`} type="password" autoComplete="new-password" value={newPassword} onChange={(event) => { setNewPassword(event.target.value); setPasswordError(""); }} aria-invalid={Boolean(passwordError)} className={`${passwordError ? "border-destructive" : ""} placeholder:text-foreground placeholder:opacity-100`} placeholder={configuredPasswordMask} />{passwordError && <p className="text-sm text-destructive">{passwordError}</p>}</section>}
    {canManageApprovalCredential&&<section className="space-y-4 rounded-2xl border p-5"><div><h3 className="font-semibold">Clave de autorización de supervisor</h3><p className="text-sm text-muted-foreground">Permite que este usuario autorice una sola acción sensible en la caja, como cerrar una sesión de venta. Es independiente de su contraseña de acceso.</p></div>{approvalCredential.data?.isConfigured&&<div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-emerald-50 p-4 text-sm"><div><strong>Clave configurada</strong><p className="text-muted-foreground">{approvalCredential.data.isOneTime?"Válida para una sola autorización":approvalCredential.data.validUntil?`Vence ${new Date(approvalCredential.data.validUntil).toLocaleString("es-CO")}`:"Sin vencimiento"}</p></div>{editing&&<Button type="button" variant={revokeApprovalCredential?"outline":"destructive"} size="sm" onClick={()=>setRevokeApprovalCredential(value=>!value)}>{revokeApprovalCredential?"Conservar clave":"Revocar al guardar"}</Button>}</div>}{editing&&<div className="grid gap-4 md:grid-cols-3"><div className="space-y-2"><Label>Nueva clave</Label><Input type="password" minLength={6} maxLength={32} value={approvalSecret} onChange={event=>{setApprovalSecret(event.target.value);setRevokeApprovalCredential(false)}} autoComplete="new-password" placeholder="Mínimo 6 caracteres"/></div><div className="space-y-2"><Label>Confirmar clave</Label><Input type="password" minLength={6} maxLength={32} value={approvalConfirmation} onChange={event=>setApprovalConfirmation(event.target.value)} autoComplete="new-password"/></div><div className="space-y-2"><Label>Vigencia</Label><Select value={approvalValidity} onValueChange={value=>setApprovalValidity(value as typeof approvalValidity)}><SelectTrigger><SelectValue/></SelectTrigger><SelectContent><SelectItem value="once">Un solo uso</SelectItem><SelectItem value="8">8 horas</SelectItem><SelectItem value="168">1 semana</SelectItem><SelectItem value="always">Sin vencimiento</SelectItem></SelectContent></Select></div>{approvalConfirmation&&approvalSecret!==approvalConfirmation&&<p className="text-sm text-destructive md:col-span-3">Las claves no coinciden.</p>}<p className="text-xs text-muted-foreground md:col-span-3">La nueva clave o la revocación se aplicará al pulsar “Guardar tercero”.</p></div>}</section>}
  </div>;
}

function PanelHeader({ icon: Icon, title, description, children }: { icon: typeof Scissors; title: string; description: string; children: React.ReactNode }) { return <div className="flex flex-col justify-between gap-4 rounded-2xl bg-gradient-to-r from-slate-950 to-teal-950 p-5 text-white sm:flex-row sm:items-center"><div className="flex items-center gap-3"><span className="rounded-xl bg-white/10 p-3 text-teal-300"><Icon className="h-5 w-5" /></span><div><h3 className="font-semibold">{title}</h3><p className="text-sm text-slate-300">{description}</p></div></div>{children}</div>; }
function EmptyState({ text }: { text: string }) { return <div className="col-span-full rounded-xl border border-dashed p-5 text-center text-sm text-muted-foreground">{text}</div>; }
function PanelLoading() { return <div className="rounded-2xl border p-8 text-center text-sm text-muted-foreground">Cargando configuración...</div>; }
function PanelError({ text }: { text: string }) { return <div className="rounded-2xl border border-destructive/30 bg-destructive/5 p-5 text-sm text-destructive">{text}</div>; }
