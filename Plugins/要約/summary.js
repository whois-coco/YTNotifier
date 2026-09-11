/*
 * summary.js — YTS.dll (SummaryBridge.SummarizeAsync) と同じ挙動を Jint 上で再現する要約スクリプト。
 *
 * 実行ファイルと同じフォルダの Plugins\summary.js として配置すると、
 * YTNotifier の SummaryScriptService から summarize(videoUrl) が呼ばれる。
 *
 * 本体から公開されている関数は httpRequest(method, url, headersJson, bodyText) と log(message) の 2 つだけで、
 * 接続先は generativelanguage.googleapis.com と www.youtube.com に限定されている。
 *
 * 処理の流れ（YTS.dll の YtsClient.SummarizeStructuredAsync と同じ）:
 *   1. 動画URLから videoId を取り出す        … YoutubeExplode の VideoId.Parse 相当
 *   2. 字幕トラック一覧を取得して "ja" を選ぶ … YoutubeExplode の ClosedCaptions.GetManifestAsync 相当
 *   3. 字幕本文を取得して 1 本のテキストにする … string.Join(" ", captions.Select(c => c.Text.Trim())).Trim()
 *   4. Gemini に投げて見出し／本文に切り分ける … ParseHeadlineAndDetail
 *   5. {"headline": "...", "detail": "..."} の JSON文字列を返す
 * いずれかで失敗した場合は null を返す（本体が既存ロジックへフォールバックする）。
 */

/* ------------------------------------------------------------------ *
 * 定数
 * ------------------------------------------------------------------ */

/** 字幕の言語。YTS.dll の SummarizeStructuredAsync が "ja" 固定で渡しているのに合わせる */
var LANGUAGE_CODE = "ja";

/** YTS.dll の YtsClient コンストラクタの既定値と同じモデル */
var GEMINI_MODEL = "gemini-3.5-flash-lite";
var GEMINI_ENDPOINT =
    "https://generativelanguage.googleapis.com/v1beta/models/" + GEMINI_MODEL + ":generateContent";

/**
 * 字幕トラック一覧の取得先。YoutubeExplode 6.6.0 と同じ InnerTube の ANDROID_VR クライアントを使う。
 * watch ページのHTMLから取れる字幕URLは署名が通らず本文が空で返るため、この経路でなければならない。
 */
var YOUTUBE_SW_JS_DATA = "https://www.youtube.com/sw.js_data";
var YOUTUBE_PLAYER     = "https://www.youtube.com/youtubei/v1/player";

/** InnerTube に渡すクライアント情報（YoutubeExplode 6.6.0 と同じ値） */
var INNERTUBE_CLIENT = {
    clientName:       "ANDROID_VR",
    clientVersion:    "1.60.19",
    deviceMake:       "Oculus",
    deviceModel:      "Quest 3",
    osName:           "Android",
    osVersion:        "12L",
    platform:         "MOBILE",
    hl:               "en",
    gl:               "US",
    utcOffsetMinutes: 0
};

/** YTS.dll の YtsClient.StructuredSummaryPromptText と同一の文面 */
var PROMPT_TEXT =
    "この動画の内容を要約してください。1行目に「ざっくり言うとどんな動画か」が一言でわかる見出しを、" +
    "2行目以降に箇条書き(「・」始まり)で3〜5行程度の要約を記載してください。" +
    "1行目は「要約しますと」「この動画の内容は以下の通りです」のような前置きを含めず、内容そのものを直接書いてください。";

/*
 * 本体の ScriptHttpGateway は body を UTF-8 で送るが、Content-Type を
 * MediaTypeHeaderValue.Parse でそのまま差し替えるため、charset は明示しておく。
 */
var JSON_CONTENT_TYPE_HEADER = '{"Content-Type":"application/json; charset=utf-8"}';

/*
 * ログに応答本文などを添えるときの文字数の上限。
 * 本体側は 1メッセージ500文字・1実行あたり20回までで、超えた分は黙って捨てられるため、
 * 「何が起きたか」を先に書き、可変長の抜粋は末尾に短く載せる。
 */
var LOG_EXCERPT_LIMIT = 200;

/* ------------------------------------------------------------------ *
 * エントリポイント
 * ------------------------------------------------------------------ */

function summarize(videoUrl) {
    try {
        var videoId = parseVideoId(videoUrl);
        if (videoId === null) {
            writeLog("動画URLからvideoIdを取り出せませんでした: " + truncateForLog(videoUrl));
            return null;
        }

        var transcript = getTranscript(videoId, LANGUAGE_CODE);
        if (isNullOrWhiteSpace(transcript)) {
            // 個別の原因は getTranscript 側で記録済み。ここでは中身が空だった場合だけ補う
            if (transcript !== null) writeLog("文字起こしが空でした: " + videoId);
            return null;
        }

        var fullText = generateSummary(buildStructuredSummaryPrompt(transcript));
        if (fullText === null) return null;

        var parsed = parseHeadlineAndDetail(fullText);
        if (parsed === null) {
            writeLog("要約の見出しまたは本文が空でした: " + truncateForLog(fullText));
            return null;
        }

        var json = JSON.stringify({ headline: parsed.headline, detail: parsed.detail });
        writeLog("プラグインでの処理完了: " + videoId +
                 " (見出し " + parsed.headline.length + "文字 / 本文 " + parsed.detail.length + "文字) 見出し=" +
                 truncateForLog(parsed.headline));
        return json;
    } catch (e) {
        // SummaryBridge.SummarizeAsync が全例外を握りつぶして null を返すのと同じ挙動
        writeLog("予期しないエラーで中断しました: " + truncateForLog(e && e.message ? e.message : String(e)));
        return null;
    }
}

/* ------------------------------------------------------------------ *
 * 1. videoId の取り出し（YoutubeExplode の VideoId.TryParse 相当）
 * ------------------------------------------------------------------ */

var VIDEO_ID_PATTERNS = [
    /youtube\..+?\/watch.*?v=(.*?)(?:&|\/|$)/,
    /youtu\.be\/watch.*?v=(.*?)(?:\?|&|\/|$)/,
    /youtu\.be\/(.*?)(?:\?|&|\/|$)/,
    /youtube\..+?\/embed\/(.*?)(?:\?|&|\/|$)/,
    /youtube\..+?\/shorts\/(.*?)(?:\?|&|\/|$)/,
    /youtube\..+?\/live\/(.*?)(?:\?|&|\/|$)/
];

function parseVideoId(videoUrlOrId) {
    if (typeof videoUrlOrId !== "string" || videoUrlOrId.length === 0) return null;

    if (isValidVideoId(videoUrlOrId)) return videoUrlOrId;

    for (var i = 0; i < VIDEO_ID_PATTERNS.length; i++) {
        var m = VIDEO_ID_PATTERNS[i].exec(videoUrlOrId);
        if (m !== null && isValidVideoId(m[1])) return m[1];
    }
    return null;
}

function isValidVideoId(value) {
    return typeof value === "string" && /^[A-Za-z0-9_-]{11}$/.test(value);
}

/* ------------------------------------------------------------------ *
 * 2〜3. 字幕の取得
 * ------------------------------------------------------------------ */

function getTranscript(videoId, languageCode) {
    var tracks = getCaptionTracks(videoId);
    if (tracks === null) return null;

    if (tracks.length === 0) {
        writeLog("字幕トラックが1つも見つかりませんでした: " + videoId);
        return null;
    }

    // YoutubeExplode の manifest.TryGetByLanguage(languageCode) ?? manifest.Tracks.FirstOrDefault()
    var track = tryGetTrackByLanguage(tracks, languageCode);
    if (track === null) {
        track = tracks[0];
        writeLog("'" + languageCode + "' の字幕が無いため '" + track.languageCode + "' で代用します: " + videoId);
    }

    var transcript = fetchTranscriptText(track);
    if (!isNullOrWhiteSpace(transcript)) {
        writeLog("文字起こし完了: " + videoId + " (" + track.languageCode + ", " + transcript.length + "文字)");
    }
    return transcript;
}

function getCaptionTracks(videoId) {
    var visitorData = fetchVisitorData();
    if (visitorData === null) return null;

    var client = {};
    for (var key in INNERTUBE_CLIENT) {
        if (Object.prototype.hasOwnProperty.call(INNERTUBE_CLIENT, key)) client[key] = INNERTUBE_CLIENT[key];
    }
    client.visitorData = visitorData;

    var payload = {
        videoId:        videoId,
        contentCheckOk: true,
        context:        { client: client }
    };

    var res = httpPostJson(YOUTUBE_PLAYER, payload, "字幕トラック一覧の取得");
    if (res === null) return null;

    var player = tryParseJson(res);
    if (player === null) {
        writeLog("字幕トラック一覧の応答を解釈できませんでした: " + videoId);
        return null;
    }

    var renderer = dig(player, ["captions", "playerCaptionsTracklistRenderer", "captionTracks"]);
    if (renderer === null || typeof renderer.length !== "number") {
        // 字幕が付いていない動画のほか、視聴不可・年齢制限などで再生情報自体を取れない場合もここに来る
        var playability = dig(player, ["playabilityStatus", "status"]);
        writeLog("字幕トラック一覧を取得できませんでした: " + videoId +
                 " (playabilityStatus=" + defaultIfNull(playability, "不明") + ")");
        return null;
    }

    var tracks = [];
    for (var i = 0; i < renderer.length; i++) {
        var t = renderer[i];
        if (!t || typeof t.baseUrl !== "string" || t.baseUrl.length === 0) continue;
        tracks.push({
            url:          t.baseUrl,
            languageCode: typeof t.languageCode === "string" ? t.languageCode : "",
            languageName: extractTrackName(t)
        });
    }
    return tracks;
}

function extractTrackName(track) {
    var name = track ? track.name : null;
    if (!name) return "";
    if (typeof name.simpleText === "string") return name.simpleText;
    if (name.runs && name.runs.length > 0 && typeof name.runs[0].text === "string") return name.runs[0].text;
    return "";
}

/** YoutubeExplode の ClosedCaptionManifest.TryGetByLanguage（言語コードまたは言語名の大小文字無視一致） */
function tryGetTrackByLanguage(tracks, language) {
    for (var i = 0; i < tracks.length; i++) {
        if (equalsIgnoreCase(tracks[i].languageCode, language) ||
            equalsIgnoreCase(tracks[i].languageName, language)) {
            return tracks[i];
        }
    }
    return null;
}

/**
 * 字幕本文を取得して 1 本のテキストにする。
 * YTS.dll の string.Join(" ", track.Captions.Select(c => c.Text.Trim())).Trim() と同じ組み立て。
 * 字幕URLの fmt は JSON で受け取れる json3 に差し替える（Jint に XML パーサが無いため）。
 */
function fetchTranscriptText(track) {
    var res = httpGet(setQueryParameter(track.url, "fmt", "json3"), "字幕本文の取得");
    if (res === null) return null;

    var data = tryParseJson(res);
    if (data === null || !data.events || typeof data.events.length !== "number") {
        writeLog("字幕本文を解析できませんでした (" + track.languageCode + ", " + res.length + "バイト)");
        return null;
    }

    var texts = [];
    for (var i = 0; i < data.events.length; i++) {
        var ev = data.events[i];

        // srv3 の <w>（表示ウィンドウの定義）に相当するイベント。字幕ではないので数えない
        if (ev.id !== undefined) continue;

        // srv3 の <p> のうち d 属性を持たないもの。YoutubeExplode は継続時間を取れない字幕を読み飛ばす
        if (ev.dDurationMs === undefined) continue;

        var line = "";
        var segs = ev.segs;
        if (segs && typeof segs.length === "number") {
            for (var j = 0; j < segs.length; j++) {
                if (typeof segs[j].utf8 === "string") line += segs[j].utf8;
            }
        }

        // 空になる字幕もそのまま数える（YoutubeExplode が読み飛ばさないため、区切りの空白が1つ増える）
        texts.push(trim(line));
    }
    return trim(texts.join(" "));
}

/**
 * sw.js_data から visitorData を取り出す。
 * これを context.client に載せないと InnerTube が LOGIN_REQUIRED を返し、字幕一覧が得られない。
 */
function fetchVisitorData() {
    var res = httpGet(YOUTUBE_SW_JS_DATA, "visitorDataの取得");
    if (res === null) return null;

    // 応答の先頭に XSSI 対策の )]}' が付くので、最初の '[' から読む
    var start = res.indexOf("[");
    var data = start < 0 ? null : tryParseJson(res.substring(start));
    if (data === null) {
        writeLog("sw.js_data の応答を解釈できませんでした: " + truncateForLog(res));
        return null;
    }

    // YoutubeExplode と同じ位置を先に見て、駄目なら構造を探索する
    var direct = dig(data, [0, 2, 0, 0, 13]);
    if (isVisitorData(direct)) return direct;

    var found = findVisitorData(data, 0);
    if (found === null) {
        writeLog("sw.js_data から visitorData を取り出せませんでした（応答の構造が変わった可能性）");
    }
    return found;
}

/*
 * visitorData は protobuf を base64 にしたもので、必ず "Cg" で始まる（3文字目は訪問者IDの長さで変わる）。
 * 同じ応答に含まれる VISITOR_PRIVACY_METADATA も "Cg" 始まりだが 20文字程度しかないため、長さで切り分ける。
 */
function isVisitorData(value) {
    return typeof value === "string" && value.length >= 40 && /^Cg[A-Za-z0-9_\-%=]+$/.test(value);
}

function findVisitorData(node, depth) {
    if (depth > 8 || node === null || typeof node !== "object") return null;
    for (var key in node) {
        if (!Object.prototype.hasOwnProperty.call(node, key)) continue;
        var child = node[key];
        if (isVisitorData(child)) return child;
        var found = findVisitorData(child, depth + 1);
        if (found !== null) return found;
    }
    return null;
}

/* ------------------------------------------------------------------ *
 * 4. Gemini への要約依頼
 * ------------------------------------------------------------------ */

/** YTS.dll の BuildStructuredSummaryPrompt と同一 */
function buildStructuredSummaryPrompt(transcript) {
    return PROMPT_TEXT + "\n\n文字起こし:\n" + transcript;
}

function generateSummary(prompt) {
    // YTS.dll は GenerateContentStream で受け取ったチャンクを連結しているが、
    // httpRequest は同期の 1 往復のみのため、同じ内容が 1 回で返る generateContent を使う。
    // Mscc.GenerativeAI 3.1.0 が実際に送っている本文と同じ形（role / model も含めて合わせている）
    var payload = {
        contents: [{ role: "user", parts: [{ text: prompt }] }],
        generationConfig: {
            thinkingConfig: { thinkingLevel: "minimal" }
        },
        model: "models/" + GEMINI_MODEL
    };

    writeLog("Gemini APIへ送信: " + GEMINI_MODEL + " (プロンプト " + prompt.length + "文字)");

    var res = httpPostJson(GEMINI_ENDPOINT, payload, "Gemini要約");
    if (res === null) return null;

    var data = tryParseJson(res);
    if (data === null) {
        writeLog("Gemini の応答を解釈できませんでした: " + truncateForLog(res));
        return null;
    }

    var parts = dig(data, ["candidates", 0, "content", "parts"]);
    if (parts === null || typeof parts.length !== "number") {
        // 安全性フィルタでの遮断や出力上限での打ち切りなど、200 でも本文が返らない場合がある
        writeLog("Gemini が要約本文を返しませんでした (finishReason=" +
                 defaultIfNull(dig(data, ["candidates", 0, "finishReason"]), "不明") + ", blockReason=" +
                 defaultIfNull(dig(data, ["promptFeedback", "blockReason"]), "なし") + ")");
        return null;
    }

    // Mscc.GenerativeAI の response.Text と同じく、テキストパートを連結する
    var text = "";
    for (var i = 0; i < parts.length; i++) {
        if (typeof parts[i].text === "string") text += parts[i].text;
    }

    writeLog("Gemini APIから要約を受信: " + text.length + "文字 (finishReason=" +
             defaultIfNull(dig(data, ["candidates", 0, "finishReason"]), "不明") + ")");
    return text;
}

/* ------------------------------------------------------------------ *
 * 5. 見出しと本文への切り分け（YTS.dll の ParseHeadlineAndDetail と同一）
 * ------------------------------------------------------------------ */

function parseHeadlineAndDetail(fullText) {
    var normalized = trim(fullText.split("\r\n").join("\n"));
    if (normalized.length === 0) return null;

    var lines = normalized.split("\n");
    var headline = trim(lines[0]);
    var detail = trim(lines.slice(1).join("\n"));

    if (isNullOrWhiteSpace(headline) || isNullOrWhiteSpace(detail)) return null;

    return { headline: headline, detail: detail };
}

/* ------------------------------------------------------------------ *
 * 通信のラッパ
 * ------------------------------------------------------------------ */

/** 成功時は応答本文の文字列、失敗時は null（label は失敗をログに残すときの名前） */
function httpGet(url, label) {
    return sendRequest("GET", url, "", "", label);
}

/** 成功時は応答本文の文字列、失敗時は null（label は失敗をログに残すときの名前） */
function httpPostJson(url, payload, label) {
    return sendRequest("POST", url, JSON_CONTENT_TYPE_HEADER, JSON.stringify(payload), label);
}

function sendRequest(method, url, headersJson, bodyText, label) {
    var raw = httpRequest(method, url, headersJson, bodyText);

    var res = typeof raw === "string" ? tryParseJson(raw) : null;
    if (res === null) {
        writeLog(label + ": 本体からの応答を解釈できませんでした");
        return null;
    }

    // status=0 は本体側が「許可外の接続先・受信量超過・通信失敗」を表すために返す値
    if (res.status === 0) {
        writeLog(label + ": 通信できませんでした（接続先が許可外、応答が大きすぎる、通信エラーのいずれか）");
        return null;
    }

    if (res.status !== 200) {
        writeLog(label + ": HTTP " + res.status + " " + truncateForLog(res.body));
        return null;
    }

    if (typeof res.body !== "string" || res.body.length === 0) {
        writeLog(label + ": 応答本文が空でした");
        return null;
    }

    return res.body;
}

/* ------------------------------------------------------------------ *
 * ログ
 * ------------------------------------------------------------------ */

/**
 * 本体の log() へ1件書き出す。上限（1メッセージ500文字・1実行20回）を超えた分は本体側が黙って捨てるため、
 * ここでは呼び出し回数を数えない。log() を公開していない本体でも動くよう、存在を確かめてから呼ぶ。
 */
function writeLog(message) {
    if (typeof log === "function") log(message);
}

/** ログに載せる可変長の抜粋を、改行を潰したうえで短く切り詰める */
function truncateForLog(value) {
    if (value === null || value === undefined) return "";

    var text = trim(String(value).replace(/\s+/g, " "));
    return text.length > LOG_EXCERPT_LIMIT ? text.substring(0, LOG_EXCERPT_LIMIT) + "…" : text;
}

/* ------------------------------------------------------------------ *
 * 小物
 * ------------------------------------------------------------------ */

function tryParseJson(text) {
    try {
        var value = JSON.parse(text);
        return value === null ? null : value;
    } catch (e) {
        return null;
    }
}

/** 値が取れなかったときだけ代わりの表示に差し替える（ログ用） */
function defaultIfNull(value, fallback) {
    return value === null || value === undefined ? fallback : value;
}

/** path に沿って辿り、途中で辿れなくなったら null */
function dig(node, path) {
    var current = node;
    for (var i = 0; i < path.length; i++) {
        if (current === null || current === undefined || typeof current !== "object") return null;
        current = current[path[i]];
    }
    return current === undefined ? null : current;
}

/** 既存のクエリ文字列に同名パラメータがあれば置き換え、無ければ追加する */
function setQueryParameter(url, name, value) {
    var pattern = new RegExp("([?&])" + name + "=[^&]*");
    if (pattern.test(url)) return url.replace(pattern, "$1" + name + "=" + value);
    return url + (url.indexOf("?") >= 0 ? "&" : "?") + name + "=" + value;
}

/** C# の string.Trim() 相当（JavaScript の \s も全角スペースなどの Unicode 空白を含む） */
function trim(value) {
    return String(value).replace(/^\s+|\s+$/g, "");
}

function isNullOrWhiteSpace(value) {
    return value === null || value === undefined || trim(value).length === 0;
}

function equalsIgnoreCase(a, b) {
    return typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();
}
