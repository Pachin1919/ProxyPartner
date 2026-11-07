"""Optional package/spec checks, not a product dependency or product correctness test.
Requires Python 3.11+ and jsonschema. Product/Codex implementation uses C#/Go.
"""
from pathlib import Path
import hashlib,json,sqlite3,tomllib,unittest,copy,math,tempfile
import jsonschema
R=Path(__file__).resolve().parents[2]

def score(case):
    ts=case['targets']
    if any(x is None or x['n']<10 for x in ts): return None
    out=[];cap=100.0
    for i,x in enumerate(ts):
        ratio=x['ok']/x['n']
        if x['ok']==0: value=0.0
        else:
            l=max(0,min(1,(3000-x['mean_ms'])/2800))
            v=max(0,min(1,1-x['sd_ms']/1000))
            value=100*(.5*ratio+.35*l+.15*v)
        out.append(value)
        if i<2 and (x['n']-x['ok'])/x['n']>=.2:cap=min(cap,20)
        if i<2 and x['ok']>0 and x['mean_ms']>2500:cap=min(cap,40)
    return min(cap,sum(w*s for w,s in zip(case['weights'],out))/sum(case['weights']))

class PackageTests(unittest.TestCase):
    def setUp(self):
        self.schema=json.loads((R/'contracts/sample.schema.json').read_text())
        self.sample=json.loads((R/'fixtures/sample-valid.json').read_text())
    def test_all_json_toml(self):
        for f in R.rglob('*.json'):json.loads(f.read_text())
        for f in R.rglob('*.toml'):tomllib.loads(f.read_text())
    def test_schemas_are_valid(self):
        for f in (R/'contracts').glob('*.schema.json'):jsonschema.Draft202012Validator.check_schema(json.loads(f.read_text()))
    def test_good_sample(self):jsonschema.validate(self.sample,self.schema)
    def test_unknown_field_rejected(self):
        self.sample['whole_config']='secret'
        with self.assertRaises(jsonschema.ValidationError):jsonschema.validate(self.sample,self.schema)
    def test_bad_type_rejected(self):
        self.sample['latency_ms']='620'
        with self.assertRaises(jsonschema.ValidationError):jsonschema.validate(self.sample,self.schema)
    def test_success_requires_latency(self):
        self.sample['latency_ms']=None
        with self.assertRaises(jsonschema.ValidationError):jsonschema.validate(self.sample,self.schema)
    def test_timeout_null_allowed(self):
        self.sample.update(outcome='timeout',latency_ms=None,http_status=None)
        jsonschema.validate(self.sample,self.schema)
    def test_negative_latency_rejected(self):
        self.sample['latency_ms']=-1
        with self.assertRaises(jsonschema.ValidationError):jsonschema.validate(self.sample,self.schema)
    def test_ip_family_format(self):
        s=json.loads((R/'contracts/ip-observation.schema.json').read_text());x=json.loads((R/'fixtures/ip-valid.json').read_text())
        v=jsonschema.Draft202012Validator(s,format_checker=jsonschema.FormatChecker());v.validate(x)
        x['address_family']=6
        with self.assertRaises(jsonschema.ValidationError):v.validate(x)
    def test_golden_scores(self):
        for c in json.loads((R/'fixtures/scoring-golden.json').read_text())['cases']:
            got=score(c)
            if c['expected_score'] is None:self.assertIsNone(got,c['name'])
            else:self.assertAlmostEqual(got,c['expected_score'],places=8,msg=c['name'])
    def test_source_manifest(self):
        d=json.loads((R/'research/sources.json').read_text());src=d['sources']
        self.assertEqual(len({s['id'] for s in src}),len(src))
        self.assertEqual(sum(s['verification']=='this_delivery' for s in src),d['new_source_files'])
        self.assertEqual(sum(s['verification']=='prior_turn_source_read' for s in src),d['prior_source_files'])
        for s in src:
            self.assertNotEqual(Path(s['path']).name.lower(),'readme.md')
            if s['verification']=='this_delivery':self.assertEqual(len(s['git_blob_sha1']),40)
    def test_budget_consistency(self):
        b=json.loads((R/'contracts/performance-budget.json').read_text())
        self.assertEqual((b['tray_p95_mib'],b['window_p95_mib'],b['hard_peak_private_mib']),(25,35,40))
    def test_product_tasks_pending(self):
        state=json.loads((R/'workflow/state.json').read_text())
        self.assertTrue(all(x['status']=='pending' for x in state['tasks'].values()))

class DatabaseTests(unittest.TestCase):
    def setUp(self):
        self.d=sqlite3.connect(':memory:');self.d.executescript((R/'storage/schema.sql').read_text())
        self.d.execute("INSERT INTO sources VALUES('host','verge',1,0)")
        self.d.execute("INSERT INTO nodes VALUES('n','host','node','p','rev',0,0)")
        self.d.execute("INSERT INTO targets VALUES('t',1,'https://example.invalid/','fixture')")
    def tearDown(self):self.d.close()
    def insert(self,id='s',dedup=None,latency=100,status='ok',observed=1,target_revision=1):
        if dedup is None:dedup=hashlib.sha256(id.encode()).digest()
        row=(id,dedup,'host',1,'n','rev','t',target_revision,'ctx',observed,2,'isolated_probe','cold_http_ttfb',status,latency,401,'unverified','verified')
        self.d.execute('INSERT INTO samples VALUES('+','.join(['?']*len(row))+')',row)
    def test_insert_and_query(self):
        self.insert();self.assertEqual(self.d.execute('SELECT latency_ms FROM samples').fetchone()[0],100)
    def test_dedup_key_unique(self):
        self.insert(dedup=b'a'*32)
        with self.assertRaises(sqlite3.IntegrityError):self.insert('s2',b'a'*32)
    def test_sqlite_strict_and_checks(self):
        with self.assertRaises(sqlite3.Error):self.insert(latency='junk')
        with self.assertRaises(sqlite3.Error):self.insert(latency=-1)
        with self.assertRaises(sqlite3.Error):self.insert(latency=None)
    def test_target_revision_foreign_key(self):
        with self.assertRaises(sqlite3.Error):self.insert(target_revision=2)
    def test_ip_blob_validation(self):
        with self.assertRaises(sqlite3.Error):self.d.execute("INSERT INTO ip_observations VALUES('ip','n','rev',1,'ctx','echo',4,?,1,'verified')",(b'x'*16,))
    def test_batch_delete_max500(self):
        for i in range(600):self.insert(str(i),observed=i)
        self.d.commit()
        self.d.execute('DELETE FROM samples WHERE sample_id IN (SELECT sample_id FROM samples WHERE observed_at_ms<? AND observed_at_ms<=? ORDER BY observed_at_ms LIMIT 500)',(1000,1000))
        self.assertEqual(self.d.execute('SELECT count(*) FROM samples').fetchone()[0],100)
    def test_unaggregated_not_deleted(self):
        self.insert(observed=500);self.d.execute('DELETE FROM samples WHERE observed_at_ms<? AND observed_at_ms<=?',(1000,100))
        self.assertEqual(self.d.execute('SELECT count(*) FROM samples').fetchone()[0],1)
    def test_integrity(self):self.assertEqual(self.d.execute('PRAGMA integrity_check').fetchone()[0],'ok')

if __name__=='__main__':unittest.main(verbosity=2)
