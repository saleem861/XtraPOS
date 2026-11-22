# القيم الأولية لنقاط البيع

آخر تحديث: 21 نوفمبر 2026.

## الشاشة

الرئيسية ← الإعدادات ← «القيم الأولية». تظهر لمن يملك `Devices.ConfigureCurrent` (المدير والمشرف) أو `System.Administer`.

- تفتح على النقطة الحالية.
- المدير (`Devices.ManageAll` أو `System.Administer`) يختار من القائمة أي نقطة في أي فرع، أو «جميع النقاط في كل الفروع».
- المشرف يرى نقطته فقط والقائمة مقفلة.
- أول إعداد: `customerDisplay.enabled` «تفعيل شاشة الزبون». الافتراضي غير مفعّل. زر «شاشة» في نقطة البيع يتبعه.
- القائمة تُبنى من أجهزة `POS` الفعّالة غير الملغاة في `Devices` على المركز، ومعها ما في جدول الفرع. العمليات تظهر في شاشة «سجل الترحيل» العامة، لا في هذه الشاشة.

## الجداول

- الفرع: `dbo.PosDeviceSettings` (الترحيل `050`). سطر لكل نقطة في كل الفروع: `SettingsJson`، `CentralVersion`، `SyncedAtUtc`، `UpdatedSource`.
- المركز: `CentralPosDeviceSettings`، `PosDeviceSettingsAudit` (قبل وبعد)، `PosDeviceSettingsReceipts` (عملية واحدة مرة واحدة) في الترحيل `035`.
- جدولا `049` (`BranchDeviceSettingsOutbox` و `DeviceSettingsInbox`) لم يعودا مستخدمين.

## من الفرع إلى المركز: سجل الترحيل الموجود

الحفظ (`PUT /api/device-settings`) في معاملة واحدة على الفرع:

1. يدمج القيم في `PosDeviceSettings` للنقطة أو لكل النقاط المعروفة.
2. يُدرج صفاً في `BranchOperationLog` بنوع `DeviceSettings`. المصدر `PosDeviceSettings`، و`SourceEntityID` معرّف النقطة أو `00000000-…` لجميع النقاط.

`CreditOutboxWorker` ينفذه بالترتيب مع باقي العمليات عبر `PosDeviceSettingsService.RelayLoggedAsync`. على المركز: إن وُجد `OperationID` في `PosDeviceSettingsReceipts` يكتمل دون تكرار. غير ذلك يدمج كل نقطة (ولجميع النقاط: أجهزة `POS` الفعّالة غير الملغاة في `Devices` على المركز، و`CentralPosRegistry` إن لم يكن فارغاً)، ويكتب التدقيق وسطراً في `SyncChangeLog` بنوع `PosDeviceSettings` لكل نقطة تغيّرت. خطأ 208 يعني أن `035` غير مطبّق، فيتوقف الصف `Blocked` ويُعاد في الدورة التالية.

## من المركز إلى كل الفروع

`PosDeviceSettingsSyncWorker` كل دقيقة يسحب `PosDeviceSettings` من `SyncChangeLog` بعد آخر نقطة في `SyncCheckpoints` (`StreamName = PosDeviceSettings`) ويطبّق **كل النقاط** على جدول الفرع، فيتطابق الجدول في كل الفروع.

## التعارض

الأحدث لكل إعداد على حدة. عند تساوي الوقت يفوز تعديل المركز. تعديل محلي أحدث لم يُرحَّل بعد لا تمحوه نسخة أقدم من المركز.

## الواجهات

| الطلب | الصلاحية |
|---|---|
| `GET /api/device-settings/current` | أي مستخدم على النقطة |
| `GET /api/device-settings/points` | `Devices.ConfigureCurrent`؛ كل النقاط مع `Devices.ManageAll` |
| `GET /api/device-settings/history?deviceId=` | `Devices.ConfigureCurrent`؛ نقطة أخرى مع `Devices.ManageAll` |
| `PUT /api/device-settings` | `{ scope: "Device" \| "All", deviceId?, point?, values }` |
