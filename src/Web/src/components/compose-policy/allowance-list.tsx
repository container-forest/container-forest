import { useState } from "react";
import { useTranslation } from "react-i18next";
import { FieldError } from "@/components/form-messages";
import { Loader2Icon, PlusIcon, Trash2Icon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  type AllowanceKind,
  allowanceKinds,
  commonCapabilities,
  formatAllowance,
  givesRoot,
  hostNamespaces,
  isProtected,
  kindOf,
  normalizeValue,
  parseAllowance,
  valueProblem,
} from "@/features/compose-policy/model";

/** One allowance in words: what it allows and for which value; the raw string stays visible for the CLI. */
export function AllowanceLabel({ allowance }: { allowance: string }) {
  const { t } = useTranslation();
  const parsed = parseAllowance(allowance);
  if (!parsed) {
    return <span className="font-mono text-sm">{allowance}</span>;
  }

  const value =
    parsed.kind === "host" && parsed.value
      ? t(`allowances.namespaces.${parsed.value as (typeof hostNamespaces)[number]}`, { defaultValue: parsed.value })
      : parsed.value;
  return (
    <span className="grid gap-0.5">
      <span className="text-sm">
        {t(`allowances.kinds.${parsed.kind}.label`)}
        {value && <span className="font-mono">: {value}</span>}
        {parsed.kind === "path" && (
          <span className="text-muted-foreground"> · {parsed.writable ? t("allowances.readWrite") : t("allowances.readOnly")}</span>
        )}
      </span>
      <span className="text-muted-foreground font-mono text-xs">{allowance}</span>
    </span>
  );
}

/**
 * The allowances of a stack or of an environment as a list with "Remove" and "Add allowance" (ADR-0012). Changes go
 * through `onChange` with the whole new list; the aggregator validates them again.
 */
export function AllowanceList({
  allowances,
  protectedPaths,
  canChange,
  pending,
  onChange,
}: {
  allowances: readonly string[];
  protectedPaths: readonly string[];
  canChange: boolean;
  pending: boolean;
  onChange: (next: string[]) => void;
}) {
  const { t } = useTranslation();

  return (
    <div className="grid gap-3">
      {allowances.length === 0 ? (
        <p className="text-muted-foreground text-sm">{t("allowances.none")}</p>
      ) : (
        <ul className="grid gap-2">
          {allowances.map((allowance) => (
            <li key={allowance} className="flex items-start justify-between gap-3 rounded-md border p-3">
              <div className="flex min-w-0 flex-wrap items-start gap-2">
                <AllowanceLabel allowance={allowance} />
                {givesRoot(allowance, protectedPaths) && <Badge variant="destructive">{t("allowances.rootBadge")}</Badge>}
              </div>
              {canChange && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={pending}
                  onClick={() => onChange(allowances.filter((a) => a !== allowance))}
                >
                  <Trash2Icon data-icon="inline-start" />
                  <span className="sr-only sm:not-sr-only">{t("allowances.remove")}</span>
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canChange && (
        <div>
          <AddAllowanceDialog
            protectedPaths={protectedPaths}
            pending={pending}
            onAdd={(allowance) => onChange(allowances.includes(allowance) ? [...allowances] : [...allowances, allowance])}
          />
        </div>
      )}
    </div>
  );
}

function AddAllowanceDialog({
  protectedPaths,
  pending,
  onAdd,
}: {
  protectedPaths: readonly string[];
  pending: boolean;
  onAdd: (allowance: string) => void;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [kind, setKind] = useState<AllowanceKind>("path");
  const [value, setValue] = useState("");
  const [writable, setWritable] = useState(false);
  const [touched, setTouched] = useState(false);

  const input = kindOf(kind).input;
  const takesValue = input !== "flag";
  const effectiveValue = input === "namespace" && value === "" ? hostNamespaces[0] : value;
  const problem = takesValue ? valueProblem(input, effectiveValue) : undefined;
  const allowance = formatAllowance(kind, takesValue ? normalizeValue(input, effectiveValue) : null, writable);
  const root = !problem && givesRoot(allowance, protectedPaths);
  const protectedPath = kind === "path" && !problem && isProtected(normalizeValue(input, value), protectedPaths);

  const reset = () => {
    setKind("path");
    setValue("");
    setWritable(false);
    setTouched(false);
  };

  const submit = () => {
    setTouched(true);
    if (problem) return;
    onAdd(allowance);
    setOpen(false);
    reset();
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) reset();
      }}
    >
      <DialogTrigger asChild>
        <Button variant="outline" size="sm" disabled={pending}>
          {pending ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <PlusIcon data-icon="inline-start" />}
          {t("allowances.add")}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t("allowances.title")}</DialogTitle>
          <DialogDescription>{t("allowances.description")}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <div className="grid gap-2">
            <Label htmlFor="allowance-kind">{t("allowances.kind")}</Label>
            <Select
              value={kind}
              onValueChange={(next) => {
                if (next === "" || next === kind) return;
                setKind(next as AllowanceKind);
                setValue("");
                setTouched(false);
              }}
            >
              <SelectTrigger id="allowance-kind">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {allowanceKinds.map((k) => (
                  <SelectItem key={k.kind} value={k.kind}>
                    {t(`allowances.kinds.${k.kind}.label`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-muted-foreground text-sm">{t(`allowances.kinds.${kind}.hint`)}</p>
          </div>

          {input === "namespace" && (
            <div className="grid gap-2">
              <Label htmlFor="allowance-value">{t("allowances.value")}</Label>
              <Select value={effectiveValue} onValueChange={(next) => next !== "" && setValue(next)}>
                <SelectTrigger id="allowance-value">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {hostNamespaces.map((ns) => (
                    <SelectItem key={ns} value={ns}>
                      {t(`allowances.namespaces.${ns}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}

          {takesValue && input !== "namespace" && (
            <div className="grid gap-2">
              <Label htmlFor="allowance-value">{t("allowances.value")}</Label>
              <Input
                id="allowance-value"
                className={input === "port" ? "w-32" : "font-mono"}
                inputMode={input === "port" ? "numeric" : undefined}
                list={input === "capability" ? "allowance-capabilities" : undefined}
                placeholder={
                  input === "path" ? "/srv/media" : input === "device" ? "/dev/net/tun" : input === "port" ? "443" : undefined
                }
                autoComplete="off"
                spellCheck={false}
                value={value}
                aria-invalid={touched && problem ? true : undefined}
                aria-describedby={touched && problem ? "allowance-value-error" : undefined}
                onChange={(event) => setValue(event.target.value)}
              />
              {input === "capability" && (
                <datalist id="allowance-capabilities">
                  {commonCapabilities.map((c) => (
                    <option key={c} value={c} />
                  ))}
                </datalist>
              )}
              <FieldError id="allowance-value-error" message={touched ? problem : undefined} />
            </div>
          )}

          {kind === "path" && (
            <div className="grid gap-2">
              <Label htmlFor="allowance-access">{t("allowances.access")}</Label>
              <Select value={writable ? "rw" : "ro"} onValueChange={(next) => next !== "" && setWritable(next === "rw")}>
                <SelectTrigger id="allowance-access" className="w-56">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="ro">{t("allowances.readOnly")}</SelectItem>
                  <SelectItem value="rw">{t("allowances.readWrite")}</SelectItem>
                </SelectContent>
              </Select>
            </div>
          )}

          {protectedPath && <p className="text-sm">{t("allowances.protectedPath")}</p>}
          {root && <p className="text-destructive text-sm">{t("allowances.confirmRoot")}</p>}
          <p className="text-muted-foreground font-mono text-xs">{allowance}</p>

          <DialogFooter>
            <Button type="submit" variant={root ? "destructive" : "default"}>
              {root ? t("allowances.submitRoot") : t("allowances.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
