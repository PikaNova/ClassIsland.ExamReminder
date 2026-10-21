#!/usr/bin/env node
// ClassIsland 考试提醒插件「演练看板」：本地模拟 plugin-bootstrap 协议，用于逐项验证提醒。
// 仅监听 127.0.0.1，接受任意插件凭据，不访问外网。
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const STATE_FILE = path.join(HERE, "state.json");
const argv = process.argv.slice(2);
const cmd = argv[0] ?? "help";
const opt = (name, dflt) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[i + 1] : dflt;
};

const PORT = Number(opt("port", process.env.DRILL_PORT || 8787));
const HOST = "127.0.0.1";
const BASE = `http://${HOST}:${PORT}`;

function blank() {
  return {
    seq: 0,
    gradeId: "drill-grade-1",
    classId: "drill-class-1",
    classTag: "高一(3)班 · 演练",
    exams: [],
    announcements: [],
    pairedTokens: {},
    viewerOnline: true,
    failBootstrap: false,
    honorLightSync: true,
  };
}

function load() {
  try {
    return { ...blank(), ...JSON.parse(fs.readFileSync(STATE_FILE, "utf8")) };
  } catch {
    return blank();
  }
}

let state = load();
const persist = () => fs.writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
const bump = () => { state.seq += 1; persist(); };
const version = () => `drill-${state.seq}`;
const at = minutes => new Date(Date.now() + minutes * 60_000).toISOString();
const hhmm = iso => new Date(iso).toLocaleString("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false });
const shortId = () => `drill-${Date.now().toString(36)}${Math.floor(Math.random() * 1e3)}`;

function addExam(name, startIn, duration, extra = {}) {
  const exam = {
    id: extra.id ?? shortId(),
    name,
    startAt: at(startIn),
    endAt: at(startIn + duration),
    kind: extra.kind ?? "major",
    sourceName: extra.sourceName ?? "演练",
    note: extra.note ?? "演练数据，可随时删除",
  };
  state.exams.push(exam);
  bump();
  return exam;
}

const find = id => state.exams.find(x => x.id === id);
const summary = () => ({
  version: version(),
  classTag: state.classTag,
  viewerOnline: state.viewerOnline,
  failBootstrap: state.failBootstrap,
  honorLightSync: state.honorLightSync,
  exams: state.exams
    .slice()
    .sort((a, b) => a.startAt.localeCompare(b.startAt))
    .map(x => ({ id: x.id, name: x.name, start: hhmm(x.startAt), end: hhmm(x.endAt), inMinutes: Math.round((Date.parse(x.startAt) - Date.now()) / 60_000) })),
  announcements: state.announcements.length,
});

function bootstrap(body) {
  if (state.failBootstrap) return { status: 500, json: { ok: false, error: "drill failure injected" } };
  const known = body.knownVersion;
  if (state.honorLightSync && known && known === version()) {
    return {
      status: 200,
      json: { ok: true, changed: false, version: known, apiVersion: 2, schemaVersion: 2, serverTime: new Date().toISOString(), viewerOnline: state.viewerOnline, exams: [], updatedAt: Date.now() },
    };
  }
  return {
    status: 200,
    json: {
      ok: true,
      changed: true,
      version: version(),
      apiVersion: 2,
      schemaVersion: 2,
      capabilities: ["session-history"],
      serverTime: new Date().toISOString(),
      viewerOnline: state.viewerOnline,
      binding: { gradeId: state.gradeId, classId: state.classId, classTag: state.classTag },
      exams: state.exams,
      updatedAt: Date.now(),
    },
  };
}

function applyControl(body) {
  const id = body.id;
  switch (body.cmd) {
    case "status":
      return summary();
    case "clear":
      state.exams = []; bump(); return summary();
    case "reset":
      state = blank(); persist(); return summary();
    case "add": {
      const exam = addExam(body.name ?? "演练科目", Number(body.startIn ?? 3), Number(body.duration ?? 60), { id: body.id });
      return { added: exam, ...summary() };
    }
    case "remove": {
      const hit = find(id);
      state.exams = state.exams.filter(x => x.id !== id);
      bump();
      return { removed: hit?.id ?? id, ...summary() };
    }
    case "move": {
      const exam = find(id);
      if (!exam) return { error: `找不到 id=${id}` };
      const duration = Date.parse(exam.endAt) - Date.parse(exam.startAt);
      exam.startAt = at(Number(body.startIn));
      exam.endAt = new Date(Date.parse(exam.startAt) + duration).toISOString();
      bump();
      return { moved: { id: exam.id, name: exam.name, start: hhmm(exam.startAt), end: hhmm(exam.endAt) }, ...summary() };
    }
    case "rename": {
      const exam = find(id);
      if (!exam) return { error: `找不到 id=${id}` };
      exam.name = body.name;
      bump();
      return { renamed: { id: exam.id, name: exam.name }, ...summary() };
    }
    case "viewer":
      state.viewerOnline = body.value !== "off"; persist(); return summary();
    case "fail":
      state.failBootstrap = body.value === "on"; persist(); return summary();
    case "light":
      state.honorLightSync = body.value === "on"; persist(); return summary();
    case "class":
      state.classTag = body.value; bump(); return summary();
    case "announce": {
      const now = Date.now();
      state.announcements.unshift({
        id: Math.floor(now / 1000),
        title: body.title ?? "演练公告：提醒链路自检",
        content: body.content ?? "**演练公告**\n\n- 这条来自本地演练看板\n- 用于验证未读统计、只看未读与展开阅读\n- 点“全部已读”后应从未读计数中移除",
        summary: "来自本地演练看板的测试公告。",
        pinned: true,
        url: null,
        buttonLabel: null,
        created_at: now,
        updated_at: now,
        publish_at: null,
        unpublish_at: null,
      });
      state.announcements = state.announcements.slice(0, 10);
      persist();
      return { announcements: state.announcements.length };
    }
    case "preset":
      return preset(body.value);
    default:
      return { error: `未知命令 ${body.cmd}` };
  }
}

function preset(name) {
  state.exams = [];
  if (name === "quick") {
    addExam("演练·数学", 3, 5, { id: "drill-quick-a" });
  } else if (name === "natural") {
    addExam("演练·数学", 16, 20, { id: "drill-natural-a" });
  } else if (name === "change") {
    addExam("演练·数学", 25, 60, { id: "drill-change-a" });
  } else if (name === "consecutive") {
    addExam("演练·语文", 3, 5, { id: "drill-con-a" });
    addExam("演练·英语", 10, 5, { id: "drill-con-b" });
  } else if (name === "tomorrow") {
    const day = new Date(Date.now() + 24 * 3600_000);
    const slot = h => { const d = new Date(day); d.setHours(h, 0, 0, 0); return d.toISOString(); };
    state.exams.push({ id: "drill-tom-a", name: "演练·物理", startAt: slot(8), endAt: slot(9), kind: "major", sourceName: "演练", note: "次日预告用" });
    state.exams.push({ id: "drill-tom-b", name: "演练·化学", startAt: slot(15), endAt: slot(16), kind: "major", sourceName: "演练", note: "次日预告用" });
    bump();
  } else if (name === "offline") {
    addExam("演练·数学", 30, 60, { id: "drill-offline-a" });
    state.failBootstrap = true;
    persist();
  } else {
    return { error: `未知预设 ${name}（quick|natural|change|consecutive|tomorrow|offline）` };
  }
  return { preset: name, ...summary() };
}

function page(title, inner) {
  return `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${title}</title><style>body{font:16px/1.6 system-ui,"Microsoft YaHei",sans-serif;margin:40px auto;max-width:760px;color:#1b1b1b}
code{background:#f2f2f2;padding:2px 6px;border-radius:4px}li{margin:4px 0}</style><h1>${title}</h1>${inner}`;
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, BASE);
  let raw = "";
  req.on("data", c => { raw += c; if (raw.length > 1e6) req.destroy(); });
  req.on("end", () => {
    const send = (status, obj, type = "application/json") => {
      const body = type === "application/json" ? JSON.stringify(obj) : obj;
      res.writeHead(status, { "content-type": `${type}; charset=utf-8`, "cache-control": "no-store" });
      res.end(body);
    };
    let body = {};
    try { body = raw ? JSON.parse(raw) : {}; } catch { return send(400, { ok: false, error: "bad json" }); }

    if (req.method === "POST" && url.pathname === "/api/exams") {
      if (body.action === "plugin-pair-start") {
        if (body.pairToken) state.pairedTokens[body.pairToken] = { instance: body.pluginInstanceId, approved: false };
        persist();
        return send(200, { ok: true });
      }
      if (body.action === "plugin-pair-status") {
        const entry = Object.values(state.pairedTokens).find(x => x.instance === body.pluginInstanceId);
        return send(200, { ok: true, paired: !!entry?.approved, apiVersion: 2, classTag: state.classTag });
      }
      if (body.action === "plugin-bootstrap") {
        const out = bootstrap(body);
        return send(out.status, out.json);
      }
      return send(404, { ok: false, error: `演练看板不支持 action=${body.action}` });
    }

    if (req.method === "POST" && url.pathname === "/__drill") {
      const out = applyControl(body);
      persist();
      return send(200, out);
    }

    if (req.method === "GET" && url.pathname === "/__state") return send(200, { ...summary(), announcements: state.announcements });

    if (req.method === "GET" && url.pathname === "/plugin/connect") {
      const token = url.searchParams.get("token");
      if (token && state.pairedTokens[token]) state.pairedTokens[token].approved = true;
      persist();
      return send(200, page("演练看板 · 配对确认", `<p>已自动批准配对请求 <code>${token ?? "无 token"}</code>，绑定班级：<b>${state.classTag}</b>。</p>
<p>回到 ClassIsland 设置页稍等即可看到「已连接」。当前考试：</p><ul>${state.exams.map(x => `<li>${x.name} ${hhmm(x.startAt)}–${hhmm(x.endAt)} <code>${x.id}</code></li>`).join("") || "<li>（暂无考试）</li>"}</ul>
<p><a href="/exam">考试大屏页面</a> · <a href="/__state">演练状态</a></p>`), "text/html");
    }

    if (req.method === "GET" && url.pathname === "/exam") {
      return send(200, page("考试安排（演练）", `<ul>${state.exams.map(x => `<li>${x.name} ${hhmm(x.startAt)}–${hhmm(x.endAt)}</li>`).join("") || "<li>（暂无考试）</li>"}</ul>`), "text/html");
    }

    if (req.method === "GET" && url.pathname === "/api/announcements") {
      return send(200, { ok: true, announcements: state.announcements });
    }

    return send(404, { ok: false, error: `未知路径 ${url.pathname}` });
  });
});

async function control(payload) {
  const res = await fetch(`${BASE}/__drill`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(payload) });
  const out = await res.json();
  print(out, res.status);
}

function print(out, status = 200) {
  if (out?.error) return console.log(`FAIL(${status}): ${out.error}`);
  if (out?.added) console.log(`已添加：${out.added.name} ${hhmm(out.added.startAt)}–${hhmm(out.added.endAt)} id=${out.added.id}`);
  if (out?.moved) console.log(`已改期：${out.moved.name} → ${out.moved.start}–${out.moved.end}`);
  if (out?.renamed) console.log(`已改名：${out.renamed.name}`);
  if (out?.removed) console.log(`已删除：${out.removed}`);
  if (out?.preset) console.log(`预设 ${out.preset} 已就绪`);
  if (out?.announcements !== undefined && !out.exams) console.log(`公告条数：${out.announcements}`);
  if (out.exams) {
    console.log(`版本 ${out.version} · 班级 ${out.classTag} · viewerOnline=${out.viewerOnline} · 同步注入失败=${out.failBootstrap} · 轻量同步=${out.honorLightSync}`);
    if (!out.exams.length) console.log("  （当前没有考试）");
    for (const x of out.exams) console.log(`  ${x.name}  ${x.start} → ${x.end}  (${x.inMinutes >= 0 ? `${x.inMinutes} 分钟后开考` : `已开考 ${-x.inMinutes} 分钟`})  id=${x.id}`);
  }
}

if (cmd === "serve") {
  state.classTag = opt("class", state.classTag);
  persist();
  server.listen(PORT, HOST, () => {
    console.log(`演练看板已启动：${BASE}`);
    console.log(`在 ClassIsland「考试提醒 → 连接与高级 → 看板地址」填 ${BASE} 并连接（会自动批准配对）。`);
    console.log(`另开一个终端执行 node drill.mjs help 查看全部演练命令。Ctrl+C 停止。`);
    print(summary());
  });
} else if (cmd === "status") {
  control({ cmd: "status" });
} else if (cmd === "preset") {
  control({ cmd: "preset", value: argv[1] });
} else if (cmd === "add") {
  control({ cmd: "add", name: opt("name", "演练科目"), startIn: opt("in", 3), duration: opt("duration", 60) });
} else if (cmd === "move") {
  control({ cmd: "move", id: argv[1], startIn: opt("in", 30) });
} else if (cmd === "rename") {
  control({ cmd: "rename", id: argv[1], name: opt("name", "演练·改名科目") });
} else if (cmd === "remove") {
  control({ cmd: "remove", id: argv[1] });
} else if (cmd === "clear") {
  control({ cmd: "clear" });
} else if (cmd === "reset") {
  control({ cmd: "reset" });
} else if (cmd === "viewer") {
  control({ cmd: "viewer", value: argv[1] ?? "on" });
} else if (cmd === "fail") {
  control({ cmd: "fail", value: argv[1] ?? "on" });
} else if (cmd === "light") {
  control({ cmd: "light", value: argv[1] ?? "off" });
} else if (cmd === "class") {
  control({ cmd: "class", value: argv.slice(1).join(" ") });
} else if (cmd === "announce") {
  control({ cmd: "announce", title: opt("title") });
} else if (cmd === "url") {
  console.log(BASE);
} else {
  console.log(`用法：node drill.mjs <命令> [参数] [--port ${PORT}]

  serve                     启动演练看板（另开终端跑其它命令；Ctrl+C 停止）
  url                       打印要填进插件「看板地址」的网址
  status                    查看当前演练考试与开关状态
  preset quick              3 分钟后开考、时长 5 分钟：约 8 分钟跑完考前→开考→结束前→结束（需把考前提醒改成 2、结束前提醒改成 1）
  preset natural            16 分钟后开考、时长 20 分钟：用默认 15/5 节点跑完整链路，约 36 分钟
  preset change             造一场 25 分钟后的考试，随后用 move/rename/remove 触发变更与取消提醒
  preset consecutive        语文 3 分钟后 5 分钟、英语 10 分钟后 5 分钟：验证连考避让
  preset tomorrow           明天 8:00 与 15:00 各一场：验证次日预告
  preset offline            有考试但同步注入失败：验证离线数据标注
  add --name 数学 --in 3 --duration 60
  move <id> --in 30         改到 30 分钟后（保持时长）→ 安排变更提醒
  rename <id> --name 英语    改名 → 安排变更提醒
  remove <id> / clear       删除 / 清空（未结束的考试消失 → 取消提醒）
  viewer on|off             控制 viewerOnline（考前自动打开看板判定）
  fail on|off               让 plugin-bootstrap 返回 500，验证同步失败与离线标注
  light on|off              是否按 knownVersion 返回 changed:false（验证轻量同步/完整校验）
  class 高一(3)班            改绑定的班级标签
  announce                  追加一条演练公告（设置页「作者公告」点刷新）
  reset                     恢复演练看板初始状态`);
}
