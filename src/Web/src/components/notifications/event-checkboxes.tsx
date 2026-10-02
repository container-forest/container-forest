import { useTranslation } from "react-i18next";
import { FieldError } from "@/components/form-messages";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { isRaisedYet, notificationEventKinds, toggleKind } from "@/features/notifications/model";

/** A checkbox per event kind; kinds the aggregator does not raise yet are marked, so nobody waits for them. */
export function EventCheckboxes({
  idPrefix,
  value,
  error,
  onChange,
}: {
  idPrefix: string;
  value: readonly string[];
  error: string | undefined;
  onChange: (events: ReturnType<typeof toggleKind>) => void;
}) {
  const { t } = useTranslation();

  return (
    <fieldset className="grid gap-2" aria-describedby={error ? `${idPrefix}-events-error` : undefined}>
      <legend className="mb-1 text-sm font-medium">{t("notifications.rules.form.events")}</legend>
      <div className="grid gap-2">
        {notificationEventKinds.map((kind) => (
          <div key={kind} className="flex items-center gap-2">
            <Checkbox
              id={`${idPrefix}-${kind}`}
              checked={value.includes(kind)}
              onCheckedChange={(checked) => onChange(toggleKind(value, kind, checked === true))}
            />
            <Label htmlFor={`${idPrefix}-${kind}`} className="font-normal">
              {t(`notifications.events.${kind}`)}
              {!isRaisedYet(kind) && <span className="text-muted-foreground text-xs">{t("notifications.rules.form.soon")}</span>}
            </Label>
          </div>
        ))}
      </div>
      <FieldError id={`${idPrefix}-events-error`} message={error} />
    </fieldset>
  );
}
