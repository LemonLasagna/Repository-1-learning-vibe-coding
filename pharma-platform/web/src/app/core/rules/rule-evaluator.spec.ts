import golden from '../../../../../tests/rules-golden/core.json';
import { canonicalJson, evaluate, Json, MAX_DEPTH, MAX_NODES, RuleError, validate } from './rule-evaluator';

interface GoldenCase {
  name: string;
  rule: Json;
  data: Json;
  expected: Json;
}

describe('rule evaluator: parity with C# (tests/rules-golden/core.json)', () => {
  const cases = golden as unknown as GoldenCase[];

  it('loads the shared golden vectors', () => {
    expect(cases.length).toBeGreaterThanOrEqual(37);
  });

  for (const c of cases) {
    it(c.name, () => {
      expect(canonicalJson(evaluate(c.rule, c.data))).toBe(canonicalJson(c.expected));
    });
  }
});

describe('rule evaluator: safety limits', () => {
  it('rejects unknown operators at validation', () => {
    expect(() => validate({ exec: ['rm -rf /'] })).toThrow(RuleError);
  });

  it('rejects operator objects with two keys', () => {
    expect(() => validate({ '==': [1, 1], '!=': [1, 2] })).toThrow(RuleError);
  });

  it('enforces the depth limit', () => {
    let rule: Json = true;
    for (let i = 0; i < MAX_DEPTH + 2; i++) rule = { not: rule };
    expect(() => validate(rule)).toThrow(/depth/);
  });

  it('enforces the size limit', () => {
    const big = Array.from({ length: MAX_NODES + 1 }, (_, i) => i);
    expect(() => validate({ '+': big })).toThrow(/size/);
  });

  it('treats division by zero as an error, not Infinity', () => {
    expect(() => evaluate({ '/': [1, 0] }, null)).toThrow(RuleError);
  });

  it('rejects arithmetic on text', () => {
    expect(() => evaluate({ '+': ['1', 2] }, null)).toThrow(RuleError);
  });
});

describe('canonical JSON', () => {
  it('matches the C# canonical form', () => {
    expect(canonicalJson({ b: 1, a: { z: true, y: [2, { d: null, c: 'x' }] } })).toBe(
      '{"a":{"y":[2,{"c":"x","d":null}],"z":true},"b":1}',
    );
    expect(canonicalJson({ aa: 1, a: 2, B: 3 })).toBe('{"B":3,"a":2,"aa":1}');
    expect(canonicalJson(1.5)).toBe('1.5');
    expect(canonicalJson(100)).toBe('100');
    expect(canonicalJson(-0)).toBe('0');
    expect(canonicalJson(0.000001)).toBe('0.000001');
  });
});
