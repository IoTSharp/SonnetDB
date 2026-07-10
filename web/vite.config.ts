import { fileURLToPath, URL } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';
import childProcess from 'node:child_process';
import { env } from 'node:process';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const baseFolder =
  env.APPDATA && env.APPDATA !== ''
    ? path.join(env.APPDATA, 'ASP.NET', 'https')
    : path.join(env.HOME ?? '.', '.aspnet', 'https');

const certificateName = 'sonnetdb-admin';
const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

const target =
  env.ASPNETCORE_HTTPS_PORT && env.ASPNETCORE_HTTPS_PORT !== ''
    ? `https://localhost:${env.ASPNETCORE_HTTPS_PORT}`
    : env.ASPNETCORE_URLS
      ? env.ASPNETCORE_URLS.split(';')[0]
      : 'https://localhost:60844';

export default defineConfig(({ mode }) => {
  const useHttps = mode !== 'e2e';
  if (useHttps) {
    if (!fs.existsSync(baseFolder)) {
      fs.mkdirSync(baseFolder, { recursive: true });
    }

    if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
      const result = childProcess.spawnSync(
        'dotnet',
        ['dev-certs', 'https', '--export-path', certFilePath, '--format', 'Pem', '--no-password'],
        { stdio: 'inherit' },
      );

      if (result.status !== 0) {
        throw new Error('Could not create HTTPS certificate for SonnetDB Admin.');
      }
    }
  }

  return {
    base: '/',
    plugins: [vue()],
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },
    build: {
      target: 'es2022',
      outDir: 'dist',
      emptyOutDir: true,
      sourcemap: false,
      rollupOptions: {
        output: {
          manualChunks: {
            vue: ['vue', 'vue-router', 'pinia'],
            naive: ['naive-ui'],
          },
        },
      },
    },
    server: {
      port: Number.parseInt(env.DEV_SERVER_PORT ?? '5173', 10),
      strictPort: true,
      https: useHttps ? {
        cert: fs.readFileSync(certFilePath),
        key: fs.readFileSync(keyFilePath),
      } : undefined,
      proxy: {
        '^/(v1|healthz|metrics|help|mcp)': {
          target,
          secure: false,
        },
      },
    },
  };
});
