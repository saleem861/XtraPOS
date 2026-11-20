# XtraPOS — مرجع جلسة 20 نوفمبر 2026

**التاريخ:** 20 نوفمبر 2026، حتى الساعة 16:05 بتوقيت الخليل.  
**المستودع:** `https://github.com/saleem861/XtraPOS.git` على الفرع `main`.  
**مرجع الجلسة على `main`:** `715ced7` أو أي إيداع بعده.  
**المسار على جهاز التطوير السابق:** `D:\Google\XtraPOS`.  
**الغرض:** يقرأ عميل Cursor على الجهاز الجديد هذا الملف قبل أي تعديل. الكود على `main`. النسخة التي تعمل على جهاز الكاشير لم تُستبدل بعد.

لا تنسخ هذا الملف إلى مكان فيه أسرار. لا تطبع كلمات سر أو مفاتيح `.ssh` أو محتوى `appsettings.json` أو `App_Data`.

---

## 1. ما يُسحب على الجهاز الجديد

```text
git clone https://github.com/saleem861/XtraPOS.git
```

إذا كانت النسخة موجودة:

```text
Set-Location D:\Google\XtraPOS
git -c gc.auto=0 fetch origin main
git -c gc.auto=0 merge --ff-only origin/main
git log -1 --oneline
```

تأكد أن هذه الجلسة وصلت:

```text
git merge-base --is-ancestor 715ced7 HEAD
```

الخروج بلا رسالة يعني أن الجهاز على `715ced7` أو بعده. `git log -1 --oneline` يظهر رأس `main`. إذا ظهرت العبارة `did not send all necessary objects` فالسحب لم يكتمل، ولا يُشغَّل النشر قبل نجاحه. إذا سأل (`Git`) عن حذف كائنات غير مرجعية فالجواب `n`.

---

## 2. جرد الجرار الذي أصبح في الكود

الإيداع الأول `e2d2e85`، ثم التصحيحات حتى `0d0ffee`.

- الفئات: 0.5 و 1 و 2 و 5 و 10 و 20 و 50 و 100 و 200 شيكل، ويمكن إضافة فئة جديدة من شاشة الإعداد. الفئة شركة كاملة، بلا فرع ولا نقطة بيع.
- النقص يُسجَّل على الموظف. أي فارق في العد النهائي يحتاج تبرير الكاشير.
- كل تأكيد يُحفظ في سجل الجرد وفي سجل الترحيل (`BranchOperationLog`). العد غير المكتوب غير مكتمل.
- إعادة فتح الجرار تبدأ من العد الأول. الجرود السابقة تبقى في السجل.
- بعد العد الأول إذا اختلف المجموع، تظهر فقط: «المجموع المعدود لا يطابق الرصيد المتوقع. أعد العد.» وتبقى قيمة العد الأول ظاهرة، والعد الجاري هو العد الثاني.
- شاشة الإتمام بعد الحفظ تعرض النتيجة والفارق، وإذا كان نقصاً تظهر ملاحظة خصمه على الموظف.
- قرار المشرف أو المدير يكون على العد الثاني فقط، ويُحفظ صفاً جديداً ولا يغيّر أرقام العد.
- المدير والمشرف يفتحان شاشة المراجعة من أي فرع ويريان كل الفروع. الكاشير لا يراها. الجرار يُفتح من زر «الجرار» في نقطة البيع.
- أثناء الحفظ تظهر ساعة انتظار والنص «جاري حفظ العد» حتى تظهر النتيجة.
- كل خانات الفئات تبقى فوق لوحة المفاتيح. الشبكة ثلاثة أعمدة، وعدد الصفوف يزيد مع عدد الفئات. اسم الفئة والقيمة بحجم أوضح، وحقل الكمية أقصر من امتداد الصف كاملاً.
- فهرس `UX_CashDrawerCounts_OpenAttempt` أُسقط في الترحيل `048` على الفرع و`034` على المركز حتى يُحفظ عد لاحق في الوردية نفسها.

الجداول متطابقة على `XtraPOS_Branch` و`XtraPOS_Central`، ومعرّف الفرع ونقطة البيع على صفوف التشغيل.

---

## 3. ما هو منشور وما الذي يعمل على الجهاز

| الإصدار | الوقت بتوقيت الخليل | الحالة |
|---|---|---|
| `branch-agent-20261119-123133` | 19 نوفمبر 12:32 | آخر نسخة مركّبة. رأس الشاشة يظهر `20261119-123133` |
| `branch-agent-20261120-155201` | 20 نوفمبر 15:53 | نُشر من الجهاز بعد فشل السحب، وبلا تعديلات واجهة هذا اليوم |
| `branch-agent-20261120-155941` | 20 نوفمبر 16:00 | فيه تعديلات الجرد. رأس الحزمة `20261120-155941`. لم يُركَّب على `C:\Program Files\XtraPOS\BranchAgent` |

زر تحديث التطبيق ينزّل أحدث إصدار (`GitHub`): `XtraPOS-BranchAgent.zip`. النسخة العاملة لم تتبدل لأن النسخ إلى مجلد البرنامج لم يتم.

تركيب الحزمة الصحيحة، من (`PowerShell`) كمسؤول:

```text
Set-Location D:\Google\XtraPOS
$zip = "$env:TEMP\XtraPOS-BranchAgent.zip"
$stage = "$env:TEMP\XtraPOS-BranchAgent-stage"
Invoke-WebRequest -Uri "https://github.com/saleem861/XtraPOS/releases/download/branch-agent-20261120-155941/XtraPOS-BranchAgent.zip" -OutFile $zip
sc.exe stop XtraPOSBranchAgent
Get-Process XtraPOS.Api -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $stage -Force
robocopy $stage "C:\Program Files\XtraPOS\BranchAgent" /E /XD App_Data /XF appsettings*.json /R:2 /W:2
Start-Process -FilePath "C:\Program Files\XtraPOS\BranchAgent\XtraPOS.Api.exe" -WorkingDirectory "C:\Program Files\XtraPOS\BranchAgent"
```

نجاح (`robocopy`) من 0 إلى 7. بعدها يظهر في الرأس `20261120-155941`.

المسار الذي كان يستمع: `C:\Program Files\XtraPOS\BranchAgent\XtraPOS.Api.exe` على `http://127.0.0.1:47335`. الخدمة `XtraPOSBranchAgent` كانت متوقفة في الفحص السابق، والتشغيل كان من الملف مباشرة. سجل التحديث: `C:\Windows\Temp\xtrapos-release-update.log`. المحاولات الأقدم توقفت عند `robocopy 16` ولم تستبدل الملفات.

إعداد جهاز جديد ما زال ينزّل `https://test-pos.sharbelfashion.com/downloads/branch-agent.zip` عبر المثبّت `XtraPOS-BranchAgent-Setup.exe`. هذه الحزمة على الموقع لم تُرفع مع إصدار `155941`. تُرفع عندما يطبع `scripts\publish-branch-release.ps1` السطر `New device package updated` بعد سطر `Published`.

---

## 4. أمر النشر التالي

من جهاز فيه مفتاح `.ssh\xtrapos_staging_deploy` وحساب (`GitHub`) القادر على إنشاء الإصدار:

```text
Set-Location D:\Google\XtraPOS
git -c gc.auto=0 pull
powershell -ExecutionPolicy Bypass -File .\scripts\publish-branch-release.ps1
```

السكربت يبني الحزمة، وينشئ إصدار `branch-agent-` على (`GitHub`) لزر التحديث، ثم ينسخ `branch-agent.zip` وبصمته (`SHA-256`) و`branch-agent.json` إلى `/opt/xtrapos-staging/current/web/downloads`. حزمة الزر بلا `appsettings`. حزمة الجهاز الجديد تبقي `appsettings.json` لأن التثبيت الجديد لا يملك ملفاً محلياً. التحديث على جهاز مركّب لا يستبدل `App_Data` ولا `appsettings`.

---

## 5. ما يُحذف وما يبقى

حذف نواتج البناء فقط: `bin` و`obj` و`dist` و`artifacts` و`node_modules` و`desktop.ini` و`Thumbs.db`. تبقى ملفات المصدر ومجلد (`Git`) ومفتاح `.ssh` و`appsettings` و`App_Data` وقواعد `*.mdf`.

---

## 6. أسلوب المتابعة

الشرح بالعربية ومن اليمين إلى اليسار، والمصطلح الإنجليزي بين قوسين. الأوامر داخل مربع نص يبدأ بـ `Set-Location D:\Google\XtraPOS`. قواعد البيانات حقيقية: `XtraPOS_Branch` و`XtraPOS_Central`. لا تُستخدم قاعدة تجريبية مكانهما.
