import assert from 'node:assert/strict';
import { test } from 'node:test';
import { isGranted, sessionCan } from './access';
import type { SessionDto } from './generated';

// 与后端 FeatureMatcherTests 的用例保持一致：界面上的显示判断必须和服务端的权限检查同一套规则。
const cases: [grant: string, feature: string, expected: boolean][] = [
  ['*', 'tasks.manage', true],
  ['tasks.manage', 'tasks.manage', true],
  ['tasks.*', 'tasks.manage', true],
  ['tasks.*', 'tasks', false],
  ['tasks.*', 'tasksx.manage', false],
  ['tasks.view', 'tasks.manage', false],
  ['platform.*', 'platform.members.view', true],
  ['tasks.manage', 'tasks.manage.extra', false],
  ['TASKS.manage', 'tasks.manage', false],
];

for (const [grant, feature, expected] of cases) {
  test(`isGranted：授予 ${grant} 时 ${feature} ${expected ? '可用' : '不可用'}`, () => {
    assert.equal(isGranted([grant], feature), expected);
  });
}

test('isGranted：任意一项授予匹配即可', () => {
  assert.equal(isGranted(['agents.view', 'tasks.*'], 'tasks.comment'), true);
  assert.equal(isGranted([], 'tasks.comment'), false);
});

test('sessionCan：所属模块停用的功能即使角色授予了也不可用', () => {
  const session = { features: ['*'], disabledFeatures: ['crm.view'] } as unknown as SessionDto;

  assert.equal(sessionCan(session, 'tasks.manage'), true);
  assert.equal(sessionCan(session, 'crm.view'), false);
  assert.equal(sessionCan(undefined, 'tasks.manage'), false);
});
