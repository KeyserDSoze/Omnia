import { defineConfig } from 'vitest/config';
export default defineConfig({
  root: '../..',
  test: {
    include: ['tests/mobile-unit/**/*.test.ts'], environment: 'node',
    setupFiles: ['tests/mobile-unit/setup-locale.ts']
  }
});
