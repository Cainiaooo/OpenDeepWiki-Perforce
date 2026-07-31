# 使用模板将 OpenDeepWiki 一键部署到 Sealos

通过 Sealos 应用模板，你可以快速将 OpenDeepWiki 部署到云端并暴露到公网。

## 部署步骤

### 1. 进入 Sealos 应用商店
打开 [Sealos 平台](https://cloud.sealos.io/)，在左侧导航栏点击 **「应用商店」**，进入模板市场。  
![进入应用商店界面](../../img/sealos/sealos-index.jpg)

---

### 2. 打开模板调试模式
点击左下角的 **「我的应用」**，然后在右上角找到 **「在线调试模板」** 按钮。  
![打开我的应用](../../img/sealos/sealos-myapps.jpg)
![在线调试模板入口](../../img/sealos/sealos-online-debug.jpg)

---

### 3. 配置并试运行
1. 将 [sealos-template.yaml](./sealos-template.yaml) 文件内容复制到左侧输入框
2. 在右侧参数输入区域填写配置（必填项见下方说明）
3. 点击右上角 **「试运行部署」**，检查控制台是否显示 **"部署通过"**

![粘贴 YAML 文件界面](../../img/sealos/sealos-test.jpg)

---

### 4. 正式部署
确认配置无误后，点击 **「正式部署」**，等待应用状态变为 **"Running"**。  
![正式部署成功界面](../../img/sealos/sealos-deploy.jpg)

---

### 5. 访问应用
部署成功后，点击应用名称进入详情页面，可以查看：
- 外网访问地址
- 应用运行日志
- 资源使用情况

![查看部署情况](../../img/sealos/sealos-overview.jpg)
![查看应用详情](../../img/sealos/sealos-details.jpg)
![打开外网地址](../../img/sealos/sealos-web.jpg)

---

## 配置参数说明

### 必填参数

#### AI 服务配置
- **chat_api_key**: Chat 功能的 API Key（必填）
- **wiki_catalog_api_key**: Wiki 目录生成的 API Key（必填）
- **wiki_content_api_key**: Wiki 内容生成的 API Key（必填）

> 提示：如果使用同一个 API Key，三个参数填写相同的值即可

#### 模型配置
- **chat_endpoint**: Chat API 端点（默认：`https://api.openai.com/v1`）
- **wiki_catalog_model**: 目录生成模型（默认：`gpt-4o`）
- **wiki_content_model**: 内容生成模型（默认：`gpt-4o`）

#### 安全配置
- **jwt_secret_key**: JWT 密钥（生产环境务必修改默认值）

### 可选参数

#### 站点配置
- **site_url**: 公开站点 URL，用于 sitemap、robots.txt 和 SEO 元数据（默认使用 Sealos 自动分配的 Web 域名）

#### 数据库配置
- **db_type**: 数据库类型（默认：`sqlite`，可选：`postgresql`）
- **connection_string**: 数据库连接字符串

#### Wiki 生成配置
- **wiki_parallel_count**: 并行生成数量（默认：`5`）
- **wiki_languages**: 支持的语言（默认：`en,zh`）

#### 存储配置
- **volume_size_data**: 数据存储容量（默认：`5Gi`）

---

## 注意事项

1. **API Key 安全**：生产环境请使用独立的 API Key，不要使用测试 Key
2. **JWT 密钥**：务必修改默认的 JWT 密钥，使用强随机字符串
3. **资源配额**：根据实际使用情况调整存储容量和资源限制
4. **健康检查**：应用启动需要 15-30 秒，请耐心等待健康检查通过
5. **日志查看**：如遇问题，可在应用详情页查看日志进行排查

---

## 支持的 AI 提供商

OpenDeepWiki 支持以下 AI 服务提供商：

- OpenAI (GPT-4, GPT-4o 等)
- Azure OpenAI
- 其他兼容 OpenAI API 的服务（如 DeepSeek、通义千问等）

配置时需要设置：
- `chat_request_type`: 请求类型（`OpenAI` 或 `AzureOpenAI`）
- `chat_endpoint`: API 端点地址
- `chat_api_key`: API 密钥

---

## 常见问题

### Q: 部署失败怎么办？
A: 检查以下几点：
- API Key 是否正确填写
- 模型名称是否支持
- 存储容量是否足够

### Q: 如何更新配置？
A: 在 Sealos 应用详情页，点击「编辑」按钮，修改环境变量后重新部署。

### Q: 如何扩容存储？
A: 在应用详情页的「存储」标签中，可以扩展持久化卷的容量。

### Q: 支持自定义域名吗？
A: 支持。在 Ingress 配置中可以添加自定义域名，需要先在 DNS 中配置 CNAME 记录。

---

## 相关链接

- [OpenDeepWiki 官网](https://opendeep.wiki)
- [GitHub 仓库](https://github.com/AIDotNet/OpenDeepWiki)
- [Sealos 官方文档](https://sealos.io/docs)
- [完整部署文档](../../docs/content/docs/deployment/)
