# Booking State Machine

Учебная реализация **машины состояний с наблюдаемостью** для Практического занятия №4.

---

## Архитектура

```
BookingStateMachine/
├── BookingStateMachine.sln
├── src/
│   ├── BookingStateMachine.csproj   (.NET 10, Minimal API)
│   ├── Program.cs                   — DI, middleware, маршруты
│   ├── Models/
│   │   └── BookingModels.cs         — состояния, события, DTO
│   ├── Services/
│   │   ├── BookingRepository.cs     — хранилище процессов (in-memory)
│   │   └── BookingStateMachineService.cs — ядро машины состояний
│   ├── Metrics/
│   │   └── BookingMetrics.cs        — счётчики и гистограммы (OTel)
│   ├── HealthChecks/
│   │   └── BookingHealthChecks.cs   — liveness / readiness
│   └── Endpoints/
│       └── BookingEndpoints.cs      — Minimal API endpoints
└── tests/
    ├── BookingStateMachine.Tests.csproj
    ├── BookingTests.cs              — интеграционные/юнит тесты (xUnit)
    └── scenarios.http               — сценарии для REST Client
```

---

## Машина состояний

```
              ReserveRoom         SendNotification       Confirm
  Created  ────────────►  RoomReserved  ──────────►  NotificationSent  ──────►  Confirmed
                               │                           │
                               │ Cancel (компенсация)      │ Cancel (компенсация)
                               ▼                           ▼
                           Cancelled  ◄───────────────────
```

| Состояние       | Описание                                      |
|-----------------|-----------------------------------------------|
| Created         | Процесс создан, работа ещё не начата          |
| RoomReserved    | Переговорка зарезервирована (слот занят)      |
| NotificationSent| Уведомление отправлено заявителю              |
| Confirmed       | Бронь подтверждена и видна всем (финал)       |
| Cancelled       | Отменено / откат через компенсацию (финал)   |

---

## Ключевые механизмы

### Идемпотентность
Каждый запрос содержит `idempotencyKey`. Если ключ уже применялся к процессу — запрос игнорируется без изменения состояния. Это защищает от повторной доставки событий.

### Компенсация
При сбое любого шага (флаг `simulateFailure: true` или реальная ошибка) выполняется компенсирующее действие: процесс переводится в состояние `Cancelled`, в журнал добавляется запись `Compensate` с причиной.

### Сквозной идентификатор корреляции
Поле `correlationId` пишется во **все** журнальные записи (через Serilog) и в поле `AuditEntry.CorrelationId`. Это позволяет отследить полный путь конкретного запроса.

---

## Наблюдаемость

### Журналирование (Serilog)
- `[INFO]` — успешные переходы (`Transition applied`)
- `[WARN]` — дубликаты (`Duplicate delivery ignored`), компенсации (`Compensation executed`)
- `[ERR]`  — сбои шагов (`Step failed`)

### Метрики (OpenTelemetry → Prometheus)
Эндпоинт: `GET /metrics`

| Метрика                          | Тип       | Описание                                    |
|----------------------------------|-----------|---------------------------------------------|
| `booking.transitions.success`    | Counter   | Успешные переходы с тегом `event`           |
| `booking.transitions.failure`    | Counter   | Неуспешные шаги с тегом `event`             |
| `booking.deliveries.duplicate`   | Counter   | Подавленные дубликаты доставки              |
| `booking.compensations`          | Counter   | Выполненные компенсации                     |
| `booking.step.duration_ms`       | Histogram | Время выполнения шага с тегом `event`       |

### Проверки здоровья
| Эндпоинт        | Назначение                                              |
|-----------------|---------------------------------------------------------|
| `/health/live`  | Liveness: сервис жив                                   |
| `/health/ready` | Readiness: деградирует когда >80% процессов Cancelled  |

---

## Запуск

```bash
cd src
dotnet run

# Swagger UI
open https://localhost:5001/swagger

# Тесты
cd ../tests
dotnet test
```

---

## Сценарии проверки

Файл `tests/scenarios.http` содержит 4 сценария:

| # | Сценарий                          | Ожидаемый результат                         |
|---|-----------------------------------|---------------------------------------------|
| 1 | Счастливый путь (4 шага)          | Состояние `Confirmed`, 3 записи в истории   |
| 2 | Повторная доставка (один ключ ×2) | Состояние не изменилось, история = 1 запись |
| 3 | Сбой на SendNotification          | Состояние `Cancelled`, запись `Compensate`  |
| 4 | Некорректный переход (Confirm из Created) | HTTP 422 Unprocessable Entity      |

---

## Выводы

### Наблюдение 1 — Детерминизм порядка
Машина состояний явно задаёт допустимые переходы. Попытка применить событие не в той фазе немедленно отклоняется с кодом 422 и понятным сообщением. Это устраняет класс ошибок «применилось не туда».

### Наблюдение 2 — Идемпотентность
Повторная доставка события с тем же `idempotencyKey` не изменяет состояние и не создаёт новую запись в истории. Метрика `booking.deliveries.duplicate` позволяет обнаружить аномально высокую долю дубликатов в продуктиве.

### Наблюдение 3 — Компенсация
При сбое `SendNotification` система автоматически переходит в `Cancelled`. В журнале чётко видна цепочка: `Step failed → Compensation executed`. Без компенсации процесс завис бы в `RoomReserved` с занятым слотом.

### Наблюдение 4 — Сквозной correlationId
По одному UUID можно выфильтровать из Serilog все события одного бронирования: создание, каждый переход, дубликаты, компенсацию. Это критично при диагностике сбоев в продуктиве.

### Наблюдение 5 — Readiness probe
Когда доля отменённых процессов превышает 80% (критическая деградация), `/health/ready` возвращает 503. Балансировщик прекращает маршрутизировать трафик до восстановления.
