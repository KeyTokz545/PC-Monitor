package com.keytokz.pcmonitor;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputFilter;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.List;

/** Premier lancement (ou « Changer de PC ») : trouver le PC sur le Wi-Fi et taper son code. */
public class SetupActivity extends Activity {

    private LinearLayout list;
    private TextView status;
    private EditText manual;
    private volatile boolean searching;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(Ui.INK);
        getWindow().setNavigationBarColor(Ui.INK);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);

        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Ui.INK);
        scroll.setFillViewport(true);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(this, 24);
        root.setPadding(pad, Ui.dp(this, 40), pad, pad);
        scroll.addView(root);

        root.addView(Ui.text(this, "PC Monitor", 28, Ui.TEXT, true));
        TextView sub = Ui.text(this, "Choisis ton PC. Il doit être allumé, avec PC Monitor lancé, et sur le même Wi-Fi que ce téléphone.", 15, Ui.MUTED, false);
        root.addView(sub, Ui.full(this, 8));

        status = Ui.text(this, "", 14, Ui.MUTED, false);
        root.addView(status, Ui.full(this, 24));

        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        root.addView(list, Ui.full(this, 8));

        TextView again = Ui.button(this, "Chercher à nouveau", false);
        again.setOnClickListener(v -> search());
        root.addView(again, Ui.full(this, 12));

        root.addView(Ui.text(this, "PC introuvable ? Tape son adresse IP (affichée en haut du menu de l'icône PC Monitor, près de l'horloge).", 14, Ui.MUTED, false), Ui.full(this, 32));
        manual = new EditText(this);
        manual.setHint("ex. 192.168.1.75");
        manual.setHintTextColor(Ui.MUTED);
        manual.setTextColor(Ui.TEXT);
        manual.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        manual.setSingleLine(true);
        manual.setBackground(Ui.box(this, Ui.PANEL, Ui.LINE));
        manual.setPadding(Ui.dp(this, 14), Ui.dp(this, 12), Ui.dp(this, 14), Ui.dp(this, 12));
        root.addView(manual, Ui.full(this, 10));
        TextView go = Ui.button(this, "Continuer avec cette adresse", false);
        go.setOnClickListener(v -> manualConnect());
        root.addView(go, Ui.full(this, 10));

        setContentView(scroll);
        search();
    }

    private void search() {
        if (searching) return;
        searching = true;
        status.setText("Recherche du PC sur le Wi-Fi…");
        list.removeAllViews();
        new Thread(() -> {
            List<Net.Pc> found = Net.discover(getApplicationContext(), 2500);
            runOnUiThread(() -> {
                searching = false;
                if (isFinishing()) return;
                list.removeAllViews();
                if (found.isEmpty()) {
                    status.setText("Aucun PC trouvé. Vérifie que PC Monitor tourne sur le PC (icône près de l'horloge) et que le téléphone est sur le même Wi-Fi.");
                    return;
                }
                status.setText(found.size() == 1 ? "PC trouvé :" : found.size() + " PC trouvés :");
                for (Net.Pc pc : found) list.addView(pcRow(pc), Ui.full(this, 8));
            });
        }).start();
    }

    private View pcRow(Net.Pc pc) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.VERTICAL);
        row.setPadding(Ui.dp(this, 16), Ui.dp(this, 14), Ui.dp(this, 16), Ui.dp(this, 14));
        row.setBackground(Ui.box(this, Ui.PANEL, Ui.LINE));
        row.addView(Ui.text(this, pc.name, 18, Ui.TEXT, true));
        row.addView(Ui.text(this, pc.host, 14, Ui.MUTED, false));
        row.setClickable(true);
        row.setOnClickListener(v -> askCode(pc));
        return row;
    }

    private void manualConnect() {
        String host = manual.getText().toString().trim().replaceAll("^https?://", "").replaceAll("/.*$", "");
        if (host.isEmpty()) {
            Toast.makeText(this, "Tape l'adresse IP du PC", Toast.LENGTH_SHORT).show();
            return;
        }
        Net.Pc pc = new Net.Pc();
        int colon = host.lastIndexOf(':');
        if (colon > 0) {
            try {
                pc.port = Integer.parseInt(host.substring(colon + 1));
            } catch (NumberFormatException ignored) {
            }
            host = host.substring(0, colon);
        }
        final String h = host;
        pc.host = h;
        pc.name = h;
        status.setText("Connexion à " + h + "…");
        new Thread(() -> {
            StringBuilder body = new StringBuilder();
            int code = Net.get(Net.base(pc) + "/api/hello", body);
            if (code == 200) {
                try {
                    JSONObject j = new JSONObject(body.toString());
                    pc.name = j.optString("name", h);
                    JSONArray macs = j.optJSONArray("macs");
                    pc.macs = Net.joinMacs(macs);
                } catch (Exception ignored) {
                }
            }
            runOnUiThread(() -> {
                if (code == 200) {
                    status.setText("");
                    askCode(pc);
                } else {
                    status.setText("Pas de réponse de " + pc.host + ". Vérifie l'adresse et que PC Monitor tourne.");
                }
            });
        }).start();
    }

    private void askCode(Net.Pc pc) {
        EditText input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_NUMBER);
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(6)});
        input.setHint("123456");
        input.setGravity(Gravity.CENTER);
        input.setTextSize(28);
        LinearLayout wrap = new LinearLayout(this);
        int p = Ui.dp(this, 20);
        wrap.setPadding(p, Ui.dp(this, 8), p, 0);
        wrap.addView(input, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        new AlertDialog.Builder(this)
                .setTitle("Code de " + pc.name)
                .setMessage("Sur le PC : clic droit sur l'icône PC Monitor près de l'horloge, puis « Connecter un téléphone ». Tape le code à 6 chiffres.")
                .setView(wrap)
                .setPositiveButton("Connecter", (d, w) -> tryCode(pc, input.getText().toString().replaceAll("\\D", "")))
                .setNegativeButton("Annuler", null)
                .show();
        input.requestFocus();
    }

    private void tryCode(Net.Pc pc, String code) {
        if (code.length() != 6) {
            Toast.makeText(this, "Le code fait 6 chiffres", Toast.LENGTH_SHORT).show();
            return;
        }
        status.setText("Vérification du code…");
        new Thread(() -> {
            int http = Net.get(Net.base(pc) + "/api/stats?k=" + code, null);
            runOnUiThread(() -> {
                if (http == 200) {
                    Net.save(this, pc, code);
                    Intent i = new Intent(this, MainActivity.class);
                    i.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
                    startActivity(i);
                    finish();
                } else if (http == 401) {
                    status.setText("Code incorrect. Regarde le code affiché sur le PC.");
                    askCode(pc);
                } else {
                    status.setText("Le PC ne répond pas. Réessaie.");
                }
            });
        }).start();
    }
}
