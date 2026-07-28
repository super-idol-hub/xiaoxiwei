# 小曦薇 · 遥控台

从 GitHub Pages 网页给 Windows 上运行的小曦薇桌宠发送气泡消息。消息通过 Supabase 免费项目中转，网页使用 Supabase Auth 账号登录；桌宠使用只保存在本机的设备密钥接收消息。

## 部署

1. 在 Supabase 创建免费项目。
2. 打开 **SQL Editor**，执行 `supabase/schema.sql` 的全部内容。
3. 在 **Authentication -> Users** 创建并自动确认一个邮箱/密码用户。
4. 执行 `supabase/account-login-migration.sql`，把该用户绑定到指定桌宠设备。
5. 在 `Project Settings -> API` 复制 Project URL 和 Publishable key（旧项目也可使用 anon key），填入 `public/config.js`。
6. 安装依赖并构建：

   ```powershell
   pnpm install
   pnpm build
   ```

7. 使用仓库附带的 GitHub Pages workflow 发布。

部署后的网页只显示账号登录。设备配置由桌宠本地的 `xiaoxiwei-remote.json` 管理，不会出现在网页中。

## 安全设计

- 数据表启用 RLS，`anon` 和 `authenticated` 无法直接读写表。
- 网页 RPC 同时要求有效登录会话和账号—设备绑定关系。
- 匿名用户和未绑定的 Auth 用户均不能发送消息。
- 桌宠使用本地 256-bit 随机设备密钥；数据库只保存 SHA-256 摘要。
- Project URL 与 Publishable/anon key 本来就是公开值，可以提交到 GitHub；设备密钥不能提交。
- 登录密码由 Supabase Auth 保存，不写入 GitHub，也不写入网页代码。
- 消息最长 300 字，默认 24 小时过期。
