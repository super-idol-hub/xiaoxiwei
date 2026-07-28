# 小曦薇 · 遥控台

从 GitHub Pages 网页给 Windows 上运行的小曦薇桌宠发送气泡消息。消息通过 Supabase 免费项目中转，网页和 EXE 共享一份随机生成的设备密钥。

## 部署

1. 在 Supabase 创建免费项目。
2. 打开 **SQL Editor**，执行 `supabase/schema.sql` 的全部内容。
3. 在 `Project Settings -> API` 复制 Project URL 和 Publishable key（旧项目也可使用 anon key）。
4. 可将这两个公开值填入 `public/config.js`，也可在网页首次打开时填写。
5. 安装依赖并构建：

   ```powershell
   pnpm install
   pnpm build
   ```

6. 使用仓库附带的 GitHub Pages workflow 发布。

首次打开网页时，填写 Supabase 信息与设备名称。网页会下载 `xiaoxiwei-remote.json`，把它放到支持远程消息的小曦薇 EXE 同一目录即可。

## 安全设计

- 数据表启用 RLS，`anon` 和 `authenticated` 无法直接读写表。
- 所有操作只能通过受限 RPC 完成。
- 每台设备使用浏览器本地生成的 256-bit 随机密钥；数据库只保存 SHA-256 摘要。
- Project URL 与 Publishable/anon key 本来就是公开值，可以提交到 GitHub；设备密钥不能提交。
- 消息最长 300 字，默认 24 小时过期。
