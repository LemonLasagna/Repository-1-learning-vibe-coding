import { Component, computed, effect, input, signal } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Json } from '../rules/rule-evaluator';
import { computeFormStates, FieldDef, RuntimeBundle, validateBundle } from './runtime-bundle';

/**
 * Renders any entity form from its runtime bundle (FS-CFG-001/002). Hand-coded screens exist only
 * where the UX is specialised (doc 02 §2.7); everything else goes through this component.
 */
@Component({
  selector: 'pos-metadata-form',
  imports: [ReactiveFormsModule],
  templateUrl: './metadata-form.html',
  styleUrl: './metadata-form.scss',
})
export class MetadataForm {
  readonly bundle = input.required<RuntimeBundle>();
  readonly initialValue = input<Record<string, Json>>({});

  readonly form = computed(() => {
    const b = this.bundle();
    validateBundle(b);
    const init = this.initialValue();
    const controls: Record<string, AbstractControl> = {};
    for (const f of b.fields) {
      const v = init[f.code] ?? (f.type === 'boolean' ? false : null);
      controls[f.code] = new FormControl(v);
    }
    return new FormGroup(controls);
  });

  private readonly value = signal<Record<string, Json>>({});
  readonly states = computed(() => computeFormStates(this.bundle(), this.value()));
  readonly fieldsByCode = computed(() => new Map(this.bundle().fields.map((f) => [f.code, f])));

  constructor() {
    effect((onCleanup) => {
      const form = this.form();
      this.value.set(form.getRawValue());
      const sub = form.valueChanges.subscribe(() => this.value.set(form.getRawValue()));
      onCleanup(() => sub.unsubscribe());
    });

    // Push computed states into the controls. emitEvent:false avoids feedback loops.
    effect(() => {
      const form = this.form();
      const { fields } = this.states();
      for (const [code, state] of Object.entries(fields)) {
        const control = form.get(code);
        if (!control) continue;
        const def = this.fieldsByCode().get(code);
        const validators = [];
        if (state.required) validators.push(def?.type === 'boolean' ? Validators.requiredTrue : Validators.required);
        if (def?.maxLength) validators.push(Validators.maxLength(def.maxLength));
        control.setValidators(validators);
        if (state.readonly || state.hidden) control.disable({ emitEvent: false });
        else control.enable({ emitEvent: false });
        control.updateValueAndValidity({ emitEvent: false });
      }
    });
  }

  field(code: string): FieldDef {
    const f = this.fieldsByCode().get(code);
    if (!f) throw new Error(`Unknown field '${code}'`);
    return f;
  }

  control(code: string): FormControl {
    return this.form().get(code) as FormControl;
  }

  showError(code: string): boolean {
    const c = this.control(code);
    return c.invalid && (c.touched || c.dirty);
  }

  /** Marks all fields touched so errors show, and reports whether every visible, editable field is valid. */
  checkValid(): boolean {
    this.form().markAllAsTouched();
    return this.form().valid;
  }

  rawValue(): Record<string, Json> {
    return this.form().getRawValue();
  }
}
