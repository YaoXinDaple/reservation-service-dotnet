# 发布到 GitHub

建议仓库名为 `reservation-service-dotnet`，描述为：

> 用 .NET 10 和 Cassandra 5 学习查询驱动建模，包含酒店预订 API、28 项集成测试和 16 项可执行实验。

建议 topics：`cassandra`、`dotnet`、`csharp`、`aspnet-core`、`data-modeling`、`learning`。

## 首次上传

本地仓库使用 `main` 分支，已准备首次提交。提交身份使用 GitHub 的 noreply 邮箱，避免在公开提交记录中暴露个人邮箱。上传前可检查仓库状态与提交清单：

```powershell
git status --short
git show --stat --oneline HEAD
```

`.gitignore` 已排除构建产物、IDE 缓存、测试结果、日志、`.env` 文件、本地配置覆盖文件和常见私钥文件。`appsettings.json` 与 `launchSettings.json` 只包含本机示例配置，需要保留。

在 GitHub 创建名为 `reservation-service-dotnet` 的 **Public** 空仓库。创建页面不要勾选 README、`.gitignore` 或许可证，避免与本地提交产生冲突。然后执行，把 `YOUR_GITHUB_USERNAME` 替换为自己的用户名：

```powershell
git remote add origin https://github.com/YOUR_GITHUB_USERNAME/reservation-service-dotnet.git
git push -u origin main
```

也可以使用已经登录的 GitHub CLI，在本目录执行以下命令，直接创建公开仓库并推送：

```powershell
gh repo create reservation-service-dotnet --public --source . --remote origin --push --description '用 .NET 10 和 Cassandra 5 学习查询驱动建模，包含酒店预订 API、28 项集成测试和 16 项可执行实验。'
```

以上两种上传方式选择一种即可。

## 自动验证

推送代码、创建 Pull Request 或手动触发时，[CI](../.github/workflows/ci.yml) 会在 Ubuntu 上安装 `global.json` 指定的 SDK，然后构建项目、启动真实 Cassandra、运行集成测试和全部 16 项实验。无需配置 GitHub Secrets。运行结束后清理的是 CI 机器上的容器和数据卷。

可选的三节点演示不在 CI 中运行。在本机运行方法见 [实验文档](05-labs.md)。

## 来源与许可证

README 保留了参考项目及基准提交。本次准备没有替作者选择许可证；本仓库尚未包含 `LICENSE`，也未声明 MIT 或 Apache 等开源授权。

参考的 [reservation-service 基准提交](https://github.com/jeffreyscarpenter/reservation-service/tree/20a6384a39eaa87e1c4e143509ac67011875e218) 根目录中未找到许可证文件。后续确定本仓库授权方式时，需要同时核对引用内容的授权范围。
