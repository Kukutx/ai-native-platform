import next from 'eslint-config-next';

const eslintConfig = [
  ...next,
  {
    ignores: ['node_modules/**', '.next/**', 'out/**', 'next-env.d.ts'],
  },
  {
    rules: {
      'react-hooks/set-state-in-effect': 'off',
    },
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/lib/api.ts'],
    rules: {
      'no-restricted-globals': ['error', { name: 'fetch', message: '接口调用统一走 src/lib/api.ts（生成的类型安全客户端）。' }],
    },
  },
];

export default eslintConfig;
