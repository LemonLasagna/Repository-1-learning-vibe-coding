import Decimal from 'decimal.js';

/**
 * Client copy of the platform rule language (DS §10.2). It must give the same result as
 * PharmaOS.Rules.RuleEvaluator (C#) for every vector in tests/rules-golden. The server always
 * re-evaluates; the client result only drives the UX (visibility, requiredness, hints).
 *
 * Numbers are evaluated as exact decimals (28 significant digits, like System.Decimal), so
 * 0.1 + 0.2 is 0.3 here as it is on the server.
 */
export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export const MAX_DEPTH = 32;
export const MAX_NODES = 2000;

export class RuleError extends Error {}

const D = Decimal.clone({ precision: 28, rounding: Decimal.ROUND_HALF_UP });

const OPERATORS = new Set([
  'var', '==', '!=', '<', '<=', '>', '>=', 'between', 'in', 'and', 'or', 'not', 'if', 'isEmpty',
  '+', '-', '*', '/', 'min', 'max', 'abs', 'round',
]);

interface Budget {
  nodes: number;
}

export function evaluate(rule: Json, data: Json): Json {
  return evalNode(rule, data, 0, { nodes: 0 });
}

/** Configuration-time check: known operators and size/depth limits. Throws {@link RuleError}. */
export function validate(rule: Json): void {
  walk(rule, 0, { nodes: 0 });
}

export function isTruthy(v: Json): boolean {
  if (v === null) return false;
  if (Array.isArray(v)) return v.length > 0;
  switch (typeof v) {
    case 'boolean':
      return v;
    case 'number':
      return v !== 0;
    case 'string':
      return v.length > 0;
    default:
      return true;
  }
}

function count(depth: number, budget: Budget): void {
  if (depth > MAX_DEPTH) throw new RuleError(`Rule exceeds maximum depth of ${MAX_DEPTH}`);
  if (++budget.nodes > MAX_NODES) throw new RuleError(`Rule exceeds maximum size of ${MAX_NODES} nodes`);
}

function isObject(v: Json): v is { [key: string]: Json } {
  return v !== null && typeof v === 'object' && !Array.isArray(v);
}

function split(obj: { [key: string]: Json }): [string, Json[]] {
  const keys = Object.keys(obj);
  if (keys.length !== 1) throw new RuleError('An operator object must have exactly one key');
  const op = keys[0];
  const arg = obj[op];
  return [op, Array.isArray(arg) ? arg : [arg]];
}

function walk(rule: Json, depth: number, budget: Budget): void {
  count(depth, budget);
  if (Array.isArray(rule)) {
    rule.forEach((r) => walk(r, depth + 1, budget));
  } else if (isObject(rule)) {
    const [op, args] = split(rule);
    if (!OPERATORS.has(op)) throw new RuleError(`Unknown operator '${op}'`);
    args.forEach((a) => walk(a, depth + 1, budget));
  }
}

function evalNode(rule: Json, data: Json, depth: number, budget: Budget): Json {
  count(depth, budget);
  if (Array.isArray(rule)) return rule.map((r) => evalNode(r, data, depth + 1, budget));
  if (isObject(rule)) {
    const [op, args] = split(rule);
    return apply(op, args, data, depth + 1, budget);
  }
  return rule;
}

function apply(op: string, args: Json[], data: Json, depth: number, budget: Budget): Json {
  const arg = (i: number): Json => (i < args.length ? evalNode(args[i], data, depth, budget) : null);
  const nums = (): Decimal[] => {
    if (args.length === 0) throw new RuleError('Arithmetic operator requires at least one argument');
    return args.map((_, i) => requireNumber(arg(i)));
  };

  switch (op) {
    case 'var': {
      const v = resolveVar(arg(0), data);
      return v ?? (args.length > 1 ? arg(1) : null);
    }
    case '==':
      return areEqual(arg(0), arg(1));
    case '!=':
      return !areEqual(arg(0), arg(1));
    case '<':
    case '<=':
    case '>':
    case '>=':
      return compare(op, arg(0), arg(1));
    case 'between': {
      const x = arg(0);
      return compare('>=', x, arg(1)) && compare('<=', x, arg(2));
    }
    case 'in': {
      const needle = arg(0);
      const hay = arg(1);
      if (Array.isArray(hay)) return hay.some((h) => areEqual(needle, h));
      if (typeof hay === 'string' && typeof needle === 'string') return hay.includes(needle);
      return false;
    }
    case 'and':
      for (let i = 0; i < args.length; i++) if (!isTruthy(arg(i))) return false;
      return true;
    case 'or':
      for (let i = 0; i < args.length; i++) if (isTruthy(arg(i))) return true;
      return false;
    case 'not':
      return !isTruthy(arg(0));
    case 'if': {
      let i = 0;
      for (; i + 1 < args.length; i += 2) if (isTruthy(arg(i))) return arg(i + 1);
      return i < args.length ? arg(i) : null;
    }
    case 'isEmpty': {
      const v = arg(0);
      if (v === null) return true;
      if (Array.isArray(v)) return v.length === 0;
      if (isObject(v)) return Object.keys(v).length === 0;
      if (typeof v === 'string') return v.length === 0;
      return false;
    }
    case '+':
      return toJson(nums().reduce((a, b) => a.plus(b), new D(0)));
    case '*':
      return toJson(nums().reduce((a, b) => a.times(b), new D(1)));
    case '-': {
      const n = nums();
      return toJson(n.length === 1 ? n[0].negated() : n[0].minus(n[1]));
    }
    case '/': {
      const n = nums();
      if (n[1].isZero()) throw new RuleError('Division by zero');
      return toJson(n[0].dividedBy(n[1]));
    }
    case 'min':
      return toJson(D.min(...nums()));
    case 'max':
      return toJson(D.max(...nums()));
    case 'abs':
      return toJson(requireNumber(arg(0)).abs());
    case 'round': {
      const x = requireNumber(arg(0));
      const digits = args.length > 1 ? requireNumber(arg(1)).toNumber() : 0;
      const mode = args.length > 2 ? arg(2) : 'half_up';
      const rounding =
        mode === 'half_up' ? Decimal.ROUND_HALF_UP : mode === 'half_even' ? Decimal.ROUND_HALF_EVEN : null;
      if (rounding === null) throw new RuleError(`Unknown rounding mode '${String(mode)}'`);
      return toJson(x.toDecimalPlaces(digits, rounding));
    }
    default:
      throw new RuleError(`Unknown operator '${op}'`);
  }
}

function resolveVar(pathNode: Json, data: Json): Json {
  const path = pathNode === null ? '' : typeof pathNode === 'string' ? pathNode : JSON.stringify(pathNode);
  if (path === '') return data;
  let current: Json = data;
  for (const segment of path.split('.')) {
    if (Array.isArray(current) && /^\d+$/.test(segment)) {
      const idx = Number(segment);
      current = idx < current.length ? current[idx] : null;
    } else if (isObject(current)) {
      current = segment in current ? current[segment] : null;
    } else {
      return null;
    }
    if (current === null || current === undefined) return null;
  }
  return current;
}

function requireNumber(v: Json): Decimal {
  if (typeof v !== 'number') throw new RuleError(`Expected a number but got ${JSON.stringify(v)}`);
  return new D(v);
}

function toJson(d: Decimal): number {
  const n = d.toNumber();
  return Object.is(n, -0) ? 0 : n;
}

function areEqual(a: Json, b: Json): boolean {
  if (a === null || b === null) return a === null && b === null;
  if (typeof a === 'number' && typeof b === 'number') return new D(a).equals(new D(b));
  return canonicalJson(a) === canonicalJson(b);
}

/** Numbers compare numerically, strings by UTF-16 code unit; any other pairing (including null) is false. */
function compare(op: string, a: Json, b: Json): boolean {
  let cmp: number;
  if (typeof a === 'number' && typeof b === 'number') cmp = new D(a).comparedTo(new D(b));
  else if (typeof a === 'string' && typeof b === 'string') cmp = a < b ? -1 : a > b ? 1 : 0;
  else return false;
  switch (op) {
    case '<':
      return cmp < 0;
    case '<=':
      return cmp <= 0;
    case '>':
      return cmp > 0;
    default:
      return cmp >= 0;
  }
}

/** Same canonical form as PharmaOS.Gxp.CanonicalJson: sorted keys, no whitespace, normalised decimals. */
export function canonicalJson(v: Json): string {
  if (v === null) return 'null';
  if (Array.isArray(v)) return `[${v.map(canonicalJson).join(',')}]`;
  switch (typeof v) {
    case 'boolean':
      return v ? 'true' : 'false';
    case 'number': {
      const d = new D(v);
      if (d.isZero()) return '0';
      const fixed = d.toFixed();
      return fixed.includes('.') ? fixed.replace(/0+$/, '').replace(/\.$/, '') : fixed;
    }
    case 'string':
      return JSON.stringify(v);
    default: {
      const keys = Object.keys(v).sort();
      return `{${keys.map((k) => `${JSON.stringify(k)}:${canonicalJson(v[k])}`).join(',')}}`;
    }
  }
}
