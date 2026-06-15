# BankOs · Panel SuperAdmin (ASP.NET MVC)

Panel administrativo en **C# / .NET 10 (ASP.NET Core MVC)** que consume los endpoints **SuperAdmin** de la API multi‑tenant de BankOs (Laravel). Permite crear y administrar bancos (*tenants*) de forma visual, con la identidad de marca de BankOs.

> **Una plataforma. Todos los bancos.**

---

## ✨ Funcionalidades

| Módulo | Descripción |
|---|---|
| **Landing** | Página pública que explica la creación de bancos en la plataforma, con la marca y los logos de BankOs. |
| **Login** | Acceso del SuperAdmin mediante la **Master API Key** (header `X-API-Key`). Pantalla a pantalla completa con el espectro de marca. |
| **Bancos (CRUD)** | Listar, crear, ver detalle, editar, activar/desactivar y dar de baja bancos. |
| **Correos del ciclo de vida** | Correo de bienvenida con credenciales al crear, y notificaciones al actualizar / desactivar / reactivar. **Se generan desde esta app**, no desde la API. |
| **Visor de base de datos** | Exploración **solo lectura** de la base de cada banco (`tenant_{id}`): tablas y hasta 100 registros por tabla. |
| **Asistente (chatbot)** | Asistente con OpenAI (`gpt-4o-mini`) restringido a preguntas sobre los tenants del sistema. |
| **Certificado PDF** | Documento que certifica el banco y su configuración. **Se genera en la app MVC** (QuestPDF), nunca en la API, para no saturarla. |
| **Política de privacidad** | Página de privacidad incluida. |

---

## 🧩 Arquitectura (paridad con el panel de Laravel)

El panel SuperAdmin original de Laravel hace **más de lo que la API REST expone**: al crear un banco también inserta el usuario administrador en la base del tenant y edita la configuración financiera completa. Para replicar **todas** esas funciones, esta app usa un enfoque **híbrido**:

1. **API REST (`X-API-Key`)** — gobierna el ciclo de vida del tenant. Al crear un banco, la propia API crea y migra su base de datos `tenant_{id}` de forma **síncrona**.
2. **PostgreSQL directo** (solo donde la API no llega) — provisiona el primer usuario `administrador` (con hash **bcrypt** compatible con `Hash::check` de Laravel), actualiza la configuración financiera (`tenant_configs`), registra el dominio (`domains`) y resuelve el correo del administrador. Todo es **best‑effort**: si la base no es accesible, el ciclo de vida sigue funcionando por la API y la app lo informa.
3. **Correos y PDF** — se generan en esta app (SMTP y QuestPDF), nunca en la API.

### Endpoints de la API consumidos

| Método | Ruta | Uso |
|---|---|---|
| `GET` | `/api/v1/tenants?per_page=N` | Listar bancos (con su config) |
| `POST` | `/api/v1/tenants` | Crear banco |
| `GET` | `/api/v1/tenants/{slug}` | Detalle del banco |
| `PATCH` | `/api/v1/tenants/{slug}` | Actualizar **nombre** y **estado** |
| `DELETE` | `/api/v1/tenants/{slug}` | Desactivar banco (baja lógica) |
| `GET` | `/api/v1/banks` | Lista pública de bancos activos (landing) |

> La API valida en `POST`: `id, name, currency (COP|USD|EUR|GBP), max_transaction_amount, transfer_fee_type (percentage|fixed), transfer_fee_value, exchange_rates`, y `webhook_url` opcional. En `PATCH` solo acepta `name` y `status`; el resto de la configuración se escribe directo en la base (ver arquitectura).

---

## ⚙️ Requisitos

- **.NET SDK 10** ([descarga](https://dotnet.microsoft.com/download))
- Acceso de red a la API de BankOs (por defecto `http://bank-os.duckdns.org:8080`)
- *(Opcional pero recomendado)* Acceso a **PostgreSQL** del servidor (puerto **5433**) para el visor de base de datos y el aprovisionamiento del administrador
- *(Opcional)* Clave de **OpenAI** para el asistente y credenciales **SMTP** para los correos

---

## 🔧 Configuración

Edita `appsettings.json`:

```jsonc
{
  "BankOS": {
    // URL base de la API de BankOs
    "ApiBaseUrl": "http://bank-os.duckdns.org:8080"
  },
  "Email": {
    "Enabled": true,                 // pon false para desactivar el envío (se registra en logs)
    "Host": "smtp.gmail.com",
    "Port": "587",
    "Username": "tu-correo@gmail.com",
    "Password": "tu-app-password",   // contraseña de aplicación de Gmail
    "From": "tu-correo@gmail.com",
    "FromName": "BankOs SuperAdmin"
  },
  "OpenAI": {
    "ApiKey": "sk-...",              // clave de OpenAI para el asistente
    "Model": "gpt-4o-mini"
  },
  "Database": {                       // PostgreSQL para el visor y el aprovisionamiento
    "Host": "bank-os.duckdns.org",
    "Port": "5433",
    "User": "bankos",
    "Password": "secret",
    "CentralDb": "bankos_central",
    "TenantDbPrefix": "tenant_",
    "DomainSuffix": ".bank.os"
  }
}
```

### 🔑 Master API Key

Es el valor de `BANKOS_MASTER_API_KEY` del servidor (`config/bankos.php`). En desarrollo el valor por defecto es:

```
master-key-change-in-production
```

Se introduce en la pantalla de **Login** y viaja en el header `X-API-Key`. No se guarda en disco: queda en la sesión del servidor.

---

## ▶️ Ejecución

```bash
# 1) Restaurar dependencias
dotnet restore

# 2) Ejecutar
dotnet run
```

La app queda disponible en **http://localhost:5000**.

1. Abre la landing (`/`).
2. Entra al panel con la **Master API Key**.
3. Crea y administra bancos.

---

## 📁 Estructura

```
BankOsAdmin/
├─ Controllers/      Home, Auth, Dashboard, DbApi (AJAX del visor)
├─ Services/
│  ├─ BankOsApiService.cs   Cliente tipado de la API (X-API-Key)
│  ├─ TenantDbService.cs    PostgreSQL: visor (lectura) + aprovisionamiento
│  ├─ EmailService.cs       Correos del ciclo de vida (SMTP, marca BankOs)
│  └─ PdfService.cs         Certificado PDF (QuestPDF)
├─ Models/           Modelos y view models
├─ Views/            Razor (Home, Auth, Dashboard, Shared)
└─ wwwroot/          site.css (sistema de diseño), logos, favicons
```

---

## 🎨 Marca

- **Tipografías:** Sora (títulos), Inter (texto), JetBrains Mono (datos).
- **Espectro BankOs:** navy `#0c1f6e` → púrpura `#7c12fd` → azul `#0463fd` → cian `#00a8e8` → verde `#22c55e`, usado con mesura en CTA, acentos y bordes.
- Logos reales en `wwwroot/img/` (`logo.png`, `logo-text.png`, `full-logo.png`).

---

## 🛟 Notas y solución de problemas

- **El visor de BD o las métricas dicen “Sin conexión”:** la app no alcanza PostgreSQL. Verifica `Database:*` y que el puerto **5433** esté accesible (firewall) desde donde corre la app.
- **Al crear un banco aparece un aviso amarillo:** el banco se creó por la API, pero una tarea que requiere base directa (usuario admin o config) no pudo completarse. Revisa la conexión a PostgreSQL.
- **No llegan correos:** revisa `Email:*`. Con Gmail necesitas una **contraseña de aplicación**. Con `Email:Enabled=false` el envío se omite y se registra en los logs.
- **El asistente responde que no está configurado:** falta `OpenAI:ApiKey` en `appsettings.json`.
- **401 al iniciar sesión:** la Master API Key no coincide con `BANKOS_MASTER_API_KEY` del servidor.

---

## 🔒 Seguridad

- La Master API Key se mantiene en sesión del servidor (cookie `HttpOnly`), no en el navegador.
- Todas las acciones de escritura usan **token antiforgery**.
- El visor de base de datos es **solo lectura**, valida los nombres de tabla y limita los resultados.

---

© BankOs — Infraestructura financiera multi‑tenant.
