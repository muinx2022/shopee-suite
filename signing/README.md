# Ký số bản Windows — Azure Trusted Signing

Vì sao cần: nhiều máy Windows 11 bật **Smart App Control (SAC)** → **chặn mọi app chưa ký** (kể cả
`Update.exe` của Velopack → cập nhật thất bại). Ký số bằng Azure Trusted Signing khiến SAC + SmartScreen
tin ngay, client cài/cập nhật trơn tru, không cảnh báo.

> Chỉ áp dụng cho bản **Windows**. Bản Linux (AppImage) không cần — Linux không có SAC.

---

## 1. Dựng Azure Trusted Signing (làm 1 lần)

Cần tài khoản Azure có subscription (thẻ thanh toán). Gói Basic ~**$9.99/tháng**.

1. **Đăng ký resource provider**: Azure Portal → Subscription → *Resource providers* → tìm
   `Microsoft.CodeSigning` → **Register**.
2. **Tạo Trusted Signing account**: Portal → tìm **"Trusted Signing"** → *Create*. Chọn **Region** có hỗ trợ
   (vd *East US*, *West US 3*, *West Central US*, *North Europe*, *West Europe*), tier **Basic**.
   → ghi lại **Account name** + **Endpoint** (dạng `https://<region>.codesigning.azure.net/`, xem tab Overview).
3. **Identity validation** (xác minh danh tính — mất ~1–3 ngày làm việc): trong account →
   *Identity validations* → *New*. Chọn:
   - **Organization** (doanh nghiệp có giấy tờ pháp lý) hoặc **Individual** (cá nhân, xác minh CMND/hộ chiếu —
     có ở một số khu vực).
   - Loại tin cậy: **Public Trust** (để phân phối công khai).
4. **Tạo Certificate Profile**: account → *Certificate profiles* → *New* → type **Public Trust**, gắn với
   identity validation vừa được duyệt. → ghi lại **Certificate profile name**.
5. **Cấp quyền ký (RBAC)**: account (hoặc certificate profile) → *Access control (IAM)* → *Add role assignment*
   → vai trò **"Trusted Signing Certificate Profile Signer"** → gán cho tài khoản bạn dùng `az login`
   (hoặc một service principal nếu chạy CI).

Tham khảo chính thức (UI có thể đổi): Microsoft "Trusted Signing quickstart".

---

## 2. Cấu hình repo (làm 1 lần)

Chép file mẫu rồi điền 3 giá trị lấy ở trên:

```bat
copy signing\trusted-signing.example.json signing\trusted-signing.json
```

`signing/trusted-signing.json` (file này **được .gitignore**, KHÔNG commit):

```json
{
  "Endpoint": "https://eus.codesigning.azure.net/",
  "CodeSigningAccountName": "<Account name>",
  "CertificateProfileName": "<Certificate profile name>"
}
```

> Lưu **UTF-8 không BOM**. File này không chứa bí mật (xác thực qua `az login`), nhưng là cấu hình riêng theo
> tài khoản nên để ngoài git.

---

## 3. Phát hành (mỗi lần ra bản mới)

Trên máy dev **Windows**:

```bat
az login            REM đăng nhập Azure bằng tài khoản có vai trò "Certificate Profile Signer"
release-suite.cmd   REM script tự thêm --azureTrustedSignFile khi thấy signing\trusted-signing.json
```

`release-suite.cmd` tự phát hiện file cấu hình: có → **ký**; không → cảnh báo + pack **chưa ký**.
Ký số dùng `signtool.exe` (chỉ chạy trên Windows) + `DefaultAzureCredential` (lấy phiên `az login`).

Kiểm chứng đã ký: chuột phải `Releases\ShopeeSuite-win-Setup.exe` → *Properties* → tab **Digital Signatures**.
